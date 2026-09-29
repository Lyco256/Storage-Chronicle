using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.Ntfs;

/// <summary>Collects bounded Windows kernel ETW file/process observations for transient correlation.</summary>
public sealed class WindowsEtwFileIoCollector : ISourceEventCollector, IAsyncDisposable
{
    private readonly string sessionName;
    private readonly int capacity;
    private TraceEventSession? session;
    private int disposed;

    /// <summary>Initializes the ETW collector with a bounded callback queue.</summary>
    public WindowsEtwFileIoCollector(string? sessionName = null, int capacity = 2048)
    {
        this.sessionName = string.IsNullOrWhiteSpace(sessionName) ? $"StorageChronicle-{Environment.ProcessId}" : sessionName;
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 64);
        this.capacity = capacity;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceEvent> CollectAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var queue = Channel.CreateBounded<SourceEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        var stop = default(CancellationTokenRegistration);
        Task? processing = null;
        try
        {
            session = new TraceEventSession(sessionName) { StopOnDispose = true, CircularBufferMB = 8 };
            stop = cancellationToken.Register(() => session?.Stop());
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.Process);
            var source = session.Source;
            var processes = new ConcurrentDictionary<int, EtwProcess>();
            var sequence = 0L;
            var overflowed = 0;

            source.Kernel.ProcessStart += value => processes[value.ProcessID] = DescribeProcess(value);
            source.Kernel.ProcessDCStart += value => processes.TryAdd(value.ProcessID, DescribeProcess(value));
            source.Kernel.FileIOFileCreate += value => Enqueue(Create(value, CanonicalOperation.Create, null, processes, ref sequence), session, queue.Writer, ref overflowed);
            source.Kernel.FileIOFileDelete += value => Enqueue(Create(value, CanonicalOperation.Delete, null, processes, ref sequence), session, queue.Writer, ref overflowed);
            source.Kernel.FileIORename += value => Enqueue(Create(value, CanonicalOperation.Rename, null, processes, ref sequence), session, queue.Writer, ref overflowed);
            source.Kernel.FileIOWrite += value => Enqueue(Create(value, CanonicalOperation.DataWrite, null, processes, ref sequence), session, queue.Writer, ref overflowed);
            source.Kernel.FileIOSetInfo += value => Enqueue(Create(value, CanonicalOperation.MetadataChanged, null, processes, ref sequence), session, queue.Writer, ref overflowed);
            source.Kernel.FileIORead += value => Enqueue(Create(value, null, "Read", processes, ref sequence), session, queue.Writer, ref overflowed);
            source.Kernel.FileIOQueryInfo += value => Enqueue(Create(value, null, "Query", processes, ref sequence), session, queue.Writer, ref overflowed);
            source.Kernel.FileIODirEnum += value => Enqueue(Create(value, null, "DirectoryEnumeration", processes, ref sequence), session, queue.Writer, ref overflowed);

            processing = Task.Run(() =>
            {
                try { source.Process(); }
                finally { queue.Writer.TryComplete(); }
            }, CancellationToken.None);

            await foreach (var item in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item;
            await processing.ConfigureAwait(false);
            if (Volatile.Read(ref overflowed) != 0) yield return CreateGap(sequence + 1, "ETW correlation queue exceeded its bound.");
        }
        finally
        {
            session?.Dispose();
            session = null;
            stop.Dispose();
            if (processing is not null)
            {
                try { await processing.ConfigureAwait(false); } catch (Exception) { }
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) session?.Stop();
        return ValueTask.CompletedTask;
    }

    private static void Enqueue(SourceEvent value, TraceEventSession session, ChannelWriter<SourceEvent> writer, ref int overflowed)
    {
        if (Volatile.Read(ref overflowed) != 0) return;
        if (!writer.TryWrite(value))
        {
            Interlocked.Exchange(ref overflowed, 1);
            session.Stop();
            writer.TryComplete();
        }
    }

    private static SourceEvent Create(TraceEvent value, CanonicalOperation? operation, string? observation, ConcurrentDictionary<int, EtwProcess> processes, ref long sequence)
    {
        var recorded = new DateTimeOffset(value.TimeStamp.ToUniversalTime());
        var properties = ImmutableDictionary<string, string>.Empty
            .Add("sourceRoute", "WindowsKernelEtw")
            .Add("path", value is FileIONameTraceData named ? named.FileName : value.PayloadString(0, System.Globalization.CultureInfo.InvariantCulture));
        if (operation is null) properties = properties.Add("observation", observation ?? "Unknown");
        var process = processes.TryGetValue(value.ProcessID, out var known) ? known : null;
        if (process is not null)
        {
            properties = properties.Add("process.name", process.Name).Add("process.executable", process.Executable);
            if (process.Parent is not null) properties = properties.Add("process.parentInstanceId", process.Parent.Value.Value);
        }

        return new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.Etw, null, null, null, null, null, operation, null,
            new EventTime(recorded, TimeZoneInfo.Local.GetUtcOffset(recorded), recorded, DateTimeOffset.UtcNow, new SourceSequence(Interlocked.Increment(ref sequence)), new MountSequence(sequence)),
            process is null ? EventQuality.Unknown : EventQuality.Correlated, process?.Id, process is null ? ProcessAttributionQuality.Unknown : ProcessAttributionQuality.Correlated, null, null, properties);
    }

    private static EtwProcess DescribeProcess(ProcessTraceData value)
    {
        var start = new DateTimeOffset(value.TimeStamp.ToUniversalTime());
        var id = ProcessInstanceId.Create($"{value.ProcessID}:{start.Ticks}");
        ProcessInstanceId? parent = value.ParentID > 0 ? ProcessInstanceId.Create($"{value.ParentID}:unknown") : null;
        return new EtwProcess(id, value.ImageFileName ?? value.ProcessName ?? "Unknown process", value.KernelImageFileName ?? value.ImageFileName ?? "Unknown executable", parent);
    }

    private static SourceEvent CreateGap(long sequence, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        return new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.Etw, null, null, null, null, null, CanonicalOperation.UnverifiedGap, null,
            new EventTime(now, now.Offset, null, now, new SourceSequence(sequence), new MountSequence(sequence)), EventQuality.UnverifiedGap, null, ProcessAttributionQuality.Unknown, null, null,
            ImmutableDictionary<string, string>.Empty.Add("reconciliationReason", reason));
    }

    private sealed record EtwProcess(ProcessInstanceId Id, string Name, string Executable, ProcessInstanceId? Parent);
}
