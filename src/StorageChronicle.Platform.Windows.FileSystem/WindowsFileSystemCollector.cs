using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Abstractions;
using StorageChronicle.Platform.Windows.FileSystem.Monitoring;
using StorageChronicle.Platform.Windows.FileSystem.Policy;
using StorageChronicle.Platform.Windows.FileSystem.Snapshot;
using StorageChronicle.Platform.Windows.FileSystem.Volumes;

namespace StorageChronicle.Platform.Windows.FileSystem;

/// <summary>Coordinates volume enumeration, initial state, and event-driven directory monitoring.</summary>
public sealed class WindowsFileSystemCollector : ISourceEventCollector
{
    private readonly IVolumeEnumerator volumeEnumerator;
    private readonly IVolumeSnapshotReader snapshotReader;
    private readonly IWindowsDirectoryChangeMonitorFactory monitorFactory;
    private readonly WindowsExclusionPolicy exclusionPolicy;
    private readonly WindowsFileSystemOptions options;

    /// <summary>Initializes a Windows file-system collector with injectable native boundaries.</summary>
    public WindowsFileSystemCollector(IVolumeEnumerator? volumeEnumerator = null, IVolumeSnapshotReader? snapshotReader = null, IWindowsDirectoryChangeMonitorFactory? monitorFactory = null, WindowsExclusionPolicy? exclusionPolicy = null, WindowsFileSystemOptions? options = null)
    {
        this.options = (options ?? new WindowsFileSystemOptions()).Validate();
        this.volumeEnumerator = volumeEnumerator ?? new WindowsVolumeEnumerator();
        this.exclusionPolicy = exclusionPolicy ?? new WindowsExclusionPolicy(this.options);
        this.snapshotReader = snapshotReader ?? new WindowsVolumeSnapshotReader(new Interop.WindowsNativeApi(), this.exclusionPolicy, this.options);
        this.monitorFactory = monitorFactory ?? new WindowsDirectoryChangeMonitorFactory();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceEvent> CollectAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var volumes = await volumeEnumerator.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        var output = Channel.CreateUnbounded<SourceEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workers = volumes.Select(volume => CollectVolumeAsync(volume, output.Writer, linkedCancellation.Token)).ToArray();
        var completion = CompleteOutputAsync(workers, output.Writer, linkedCancellation.Token);
        try
        {
            await foreach (var sourceEvent in output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return sourceEvent;
            }

            await completion.ConfigureAwait(false);
        }
        finally
        {
            linkedCancellation.Cancel();
            try { await completion.ConfigureAwait(false); } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    private async Task CollectVolumeAsync(VolumeDescriptor volume, ChannelWriter<SourceEvent> output, CancellationToken cancellationToken)
    {
        using var volumeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var volumeToken = volumeCancellation.Token;
        try
        {
            if (!volume.IsDirectoryReadable)
            {
                await output.WriteAsync(WindowsSourceEventFactory.Gap(volume.Id, "Volume cannot be enumerated as a directory", 0), cancellationToken).ConfigureAwait(false);
                return;
            }

            var rootPath = volume.MountPoints.Count == 0 ? volume.Id.Value + Path.DirectorySeparatorChar : volume.MountPoints[0];
            var monitor = monitorFactory.Create(volume.Id, rootPath, options.NotificationBufferSize);
            var nativeReads = Channel.CreateUnbounded<DirectoryChangeRead>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var liveReads = Channel.CreateUnbounded<DirectoryChangeRead>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var monitorTask = PumpMonitorAsync(monitor, nativeReads.Writer, volumeToken);
            var pending = new InitialScanNotificationBuffer(options.InitialNotificationCapacity);
            pending.Begin(0);
            var initialGaps = new ConcurrentQueue<ContinuityGap>();
            var initialScanActive = 1;
            var distributorTask = DistributeReadsAsync(nativeReads.Reader, liveReads.Writer, pending, initialGaps, () => Volatile.Read(ref initialScanActive) == 1, volumeToken);
            var sequence = 0L;
            await foreach (var sourceEvent in snapshotReader.ReadInitialSnapshotAsync(volume, volumeToken).ConfigureAwait(false))
            {
                await output.WriteAsync(sourceEvent, volumeToken).ConfigureAwait(false);
                sequence = Math.Max(sequence, sourceEvent.Time.SourceSequence.Value + 1);
            }

            Volatile.Write(ref initialScanActive, 0);
            var buffered = pending.Complete();
            while (initialGaps.TryDequeue(out var initialGap))
            {
                await output.WriteAsync(WindowsSourceEventFactory.Gap(volume.Id, initialGap.Reason, sequence++), volumeToken).ConfigureAwait(false);
            }

            if (pending.IsOverflowed)
            {
                await output.WriteAsync(WindowsSourceEventFactory.Gap(volume.Id, "Initial scan notification buffer exceeded its bound", sequence++), volumeToken).ConfigureAwait(false);
            }

            foreach (var notification in buffered)
            {
                if (!IsExcluded(rootPath, notification))
                {
                    await output.WriteAsync(ToSourceEvent(volume, notification, sequence++), volumeToken).ConfigureAwait(false);
                }
            }

            await foreach (var read in liveReads.Reader.ReadAllAsync(volumeToken).ConfigureAwait(false))
            {
                if (read.Gap is not null)
                {
                    await output.WriteAsync(WindowsSourceEventFactory.Gap(volume.Id, read.Gap.Reason, sequence++), volumeToken).ConfigureAwait(false);
                }

                foreach (var notification in read.Notifications)
                {
                    if (!IsExcluded(rootPath, notification))
                    {
                        await output.WriteAsync(ToSourceEvent(volume, notification, sequence++), volumeToken).ConfigureAwait(false);
                    }
                }

                if (read.MonitorLost)
                {
                    break;
                }
            }

            volumeCancellation.Cancel();
            await distributorTask.ConfigureAwait(false);
            await monitorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            await output.WriteAsync(WindowsSourceEventFactory.Gap(volume.Id, "Access denied while collecting volume", 0), CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            await output.WriteAsync(WindowsSourceEventFactory.Gap(volume.Id, "Volume was removed or became unavailable", 0), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task PumpMonitorAsync(WindowsDirectoryChangeMonitor monitor, ChannelWriter<DirectoryChangeRead> writer, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var read in monitor.ReadChangesAsync(cancellationToken).ConfigureAwait(false))
            {
                await writer.WriteAsync(read, cancellationToken).ConfigureAwait(false);
                if (read.MonitorLost) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            writer.TryComplete();
        }
    }

    private static async Task DistributeReadsAsync(ChannelReader<DirectoryChangeRead> source, ChannelWriter<DirectoryChangeRead> live, InitialScanNotificationBuffer pending, ConcurrentQueue<ContinuityGap> initialGaps, Func<bool> isInitialScanActive, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var read in source.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (isInitialScanActive())
                {
                    if (read.Gap is not null)
                    {
                        initialGaps.Enqueue(read.Gap);
                    }

                    foreach (var notification in read.Notifications)
                    {
                        pending.TryAdd(notification);
                    }
                }
                else
                {
                    await live.WriteAsync(read, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            live.TryComplete();
        }
    }

    private static async Task CompleteOutputAsync(IReadOnlyList<Task> workers, ChannelWriter<SourceEvent> writer, CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            writer.TryComplete();
        }
    }

    private bool IsExcluded(string rootPath, DirectoryChangeNotification notification)
    {
        var newPath = Path.Combine(rootPath, notification.RelativePath);
        var oldPath = notification.OldRelativePath is null ? null : Path.Combine(rootPath, notification.OldRelativePath);
        return exclusionPolicy.ShouldExclude(newPath) || (oldPath is not null && exclusionPolicy.ShouldExclude(oldPath));
    }

    private static SourceEvent ToSourceEvent(VolumeDescriptor volume, DirectoryChangeNotification notification, long sequence)
    {
        var now = notification.ReceivedUtc;
        var operation = notification.Kind switch
        {
            DirectoryChangeKind.Added => CanonicalOperation.Create,
            DirectoryChangeKind.Removed => CanonicalOperation.Delete,
            DirectoryChangeKind.Modified => CanonicalOperation.DataWrite,
            DirectoryChangeKind.RenamedOldName or DirectoryChangeKind.RenamedNewName => CanonicalOperation.Rename,
            _ => CanonicalOperation.MetadataChanged
        };
        var properties = ImmutableDictionary<string, string>.Empty.Add("source", "ReadDirectoryChangesW");
        return new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.DirectoryReconciliation, volume.Id, null, null, notification.RelativePath, notification.OldRelativePath, operation, null, new EventTime(now, TimeSpan.Zero, null, now, new SourceSequence(sequence), new MountSequence(sequence)), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null, properties);
    }

}
