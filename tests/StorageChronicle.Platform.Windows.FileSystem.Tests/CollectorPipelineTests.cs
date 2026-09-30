using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Microsoft.Win32.SafeHandles;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Abstractions;
using StorageChronicle.Platform.Windows.FileSystem.Interop;
using StorageChronicle.Platform.Windows.FileSystem.Monitoring;
using Xunit;

namespace StorageChronicle.Platform.Windows.FileSystem.Tests;

public sealed class CollectorPipelineTests
{
    [Fact]
    public async Task CapacityOnePipelinePreservesSnapshotLiveNotificationAndNativeOverflowGap()
    {
        var firstVolume = CreateVolume("pipeline-volume");
        var changeNative = new FakeChangeNative(
            new NativeDirectoryChangeReadResult(BuildNotification(1, "live.txt"), BuildNotification(1, "live.txt").Length, 0),
            new NativeDirectoryChangeReadResult(Array.Empty<byte>(), 0, 1022));
        var collector = CreateCollector([firstVolume], [CreateSourceEvent(firstVolume, "snapshot.txt")], new Dictionary<string, IWindowsDirectoryChangeNative> { [firstVolume.Id.Value] = changeNative });

        var events = await ReadAllAsync(collector.CollectAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.Contains(events, item => item.Name == "snapshot.txt");
        Assert.Contains(events, item => item.Name == "live.txt" && item.Properties.GetValueOrDefault("source") == "ReadDirectoryChangesW");
        Assert.Contains(events, item => item.Hint == CanonicalOperation.UnverifiedGap && item.Properties.GetValueOrDefault("reason")?.Contains("buffer", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task OverflowOnOneVolumeDoesNotStopEventsFromAnotherVolume()
    {
        var firstVolume = CreateVolume("overflow-volume");
        var secondVolume = CreateVolume("recovered-volume");
        var collector = CreateCollector(
            [firstVolume, secondVolume],
            [CreateSourceEvent(firstVolume, "first-snapshot.txt"), CreateSourceEvent(secondVolume, "second-snapshot.txt")],
            new Dictionary<string, IWindowsDirectoryChangeNative>
            {
                [firstVolume.Id.Value] = new FakeChangeNative(new NativeDirectoryChangeReadResult(Array.Empty<byte>(), 0, 1022)),
                [secondVolume.Id.Value] = new FakeChangeNative(
                    new NativeDirectoryChangeReadResult(BuildNotification(1, "second-live.txt"), BuildNotification(1, "second-live.txt").Length, 0),
                    new NativeDirectoryChangeReadResult(Array.Empty<byte>(), 0, 1022))
            });

        var events = await ReadAllAsync(collector.CollectAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.Contains(events, item => item.VolumeId == firstVolume.Id && item.Hint == CanonicalOperation.UnverifiedGap);
        Assert.Contains(events, item => item.VolumeId == secondVolume.Id && item.Name == "second-snapshot.txt");
        Assert.Contains(events, item => item.VolumeId == secondVolume.Id && item.Name == "second-live.txt");
    }

    [Fact]
    public async Task EarlyConsumerDisposalCancelsBlockedBoundedOutputWriters()
    {
        var volume = CreateVolume("cancel-volume");
        var thirdSnapshotYielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = Enumerable.Range(0, 100).Select(index => CreateSourceEvent(volume, $"snapshot-{index}.txt")).ToArray();
        var snapshotReader = new SequenceSnapshotReader(snapshots, thirdSnapshotYielded);
        var changeNative = new FakeChangeNative(new NativeDirectoryChangeReadResult(Array.Empty<byte>(), 0, 1022));
        var collector = CreateCollector([volume], snapshotReader, new Dictionary<string, IWindowsDirectoryChangeNative> { [volume.Id.Value] = changeNative }, pipelineCapacity: 1);

        await using var enumerator = collector.CollectAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        #pragma warning disable xUnit1051
        Assert.True(await enumerator.MoveNextAsync());
        await thirdSnapshotYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = enumerator.DisposeAsync().AsTask();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        #pragma warning restore xUnit1051
    }

    private static WindowsFileSystemCollector CreateCollector(
        IReadOnlyList<VolumeDescriptor> volumes,
        IReadOnlyList<SourceEvent> events,
        IReadOnlyDictionary<string, IWindowsDirectoryChangeNative> changeNatives) =>
        CreateCollector(volumes, new SequenceSnapshotReader(events), changeNatives);

    private static WindowsFileSystemCollector CreateCollector(
        IReadOnlyList<VolumeDescriptor> volumes,
        IVolumeSnapshotReader snapshotReader,
        IReadOnlyDictionary<string, IWindowsDirectoryChangeNative> changeNatives,
        int pipelineCapacity = 1) =>
        new(
            new FixedVolumeEnumerator(volumes),
            snapshotReader,
            new TestMonitorFactory(changeNatives),
            options: new WindowsFileSystemOptions { PipelineChannelCapacity = pipelineCapacity, InitialNotificationCapacity = 2 });

    private static VolumeDescriptor CreateVolume(string id) => new(VolumeId.Create(id), "NTFS", ["C:\\collector-test-root"], false, false, false, true, true);

    private static SourceEvent CreateSourceEvent(VolumeDescriptor volume, string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.InitialSnapshot, volume.Id, FileId.Create(name), null, name, null, CanonicalOperation.Create, null, new EventTime(now, TimeSpan.Zero, null, now, new SourceSequence(0), new MountSequence(0)), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty);
    }

    private static byte[] BuildNotification(int action, string name)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var buffer = new byte[12 + nameBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), action);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8, 4), nameBytes.Length);
        nameBytes.CopyTo(buffer, 12);
        return buffer;
    }

    private static async Task<List<SourceEvent>> ReadAllAsync(IAsyncEnumerable<SourceEvent> source, CancellationToken cancellationToken)
    {
        var events = new List<SourceEvent>();
        await foreach (var item in source.WithCancellation(cancellationToken)) events.Add(item);
        return events;
    }

    private sealed class FixedVolumeEnumerator(IReadOnlyList<VolumeDescriptor> volumes) : IVolumeEnumerator
    {
        public ValueTask<IReadOnlyList<VolumeDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(volumes);
        }
    }

    private sealed class SequenceSnapshotReader(IReadOnlyList<SourceEvent> events, TaskCompletionSource? thirdYielded = null) : IVolumeSnapshotReader
    {
        public async IAsyncEnumerable<SourceEvent> ReadInitialSnapshotAsync(VolumeDescriptor volume, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var volumeEvents = events.Where(item => item.VolumeId == volume.Id).ToArray();
            for (var index = 0; index < volumeEvents.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index == 2) thirdYielded?.TrySetResult();
                yield return volumeEvents[index];
                await Task.Yield();
            }
        }
    }

    private sealed class TestMonitorFactory(IReadOnlyDictionary<string, IWindowsDirectoryChangeNative> changeNatives) : IWindowsDirectoryChangeMonitorFactory
    {
        public WindowsDirectoryChangeMonitor Create(VolumeId volumeId, string rootPath, int bufferSize) =>
            new(volumeId, rootPath, new FakeFileNative(), changeNatives[volumeId.Value], bufferSize);
    }
}
