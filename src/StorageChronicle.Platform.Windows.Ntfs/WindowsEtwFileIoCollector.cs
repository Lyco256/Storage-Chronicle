using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Abstractions;

namespace StorageChronicle.Platform.Windows.Ntfs;

/// <summary>Collects bounded Windows kernel ETW file/process observations for transient correlation.</summary>
public sealed class WindowsEtwFileIoCollector : ISourceEventCollector, IAsyncDisposable
{
    private readonly string sessionName;
    private readonly int capacity;
    private readonly IProcessLifecycleSink? processLifecycle;
    private readonly IWindowsEtwSessionFactory sessionFactory;
    private IWindowsEtwSession? session;
    private int disposed;

    /// <summary>Initializes the ETW collector with a bounded callback queue.</summary>
    public WindowsEtwFileIoCollector(string? sessionName = null, int capacity = 2048, IProcessLifecycleSink? processLifecycle = null)
        : this(sessionName, capacity, processLifecycle, new WindowsEtwSessionFactory())
    {
    }

    internal WindowsEtwFileIoCollector(string? sessionName, int capacity, IProcessLifecycleSink? processLifecycle, IWindowsEtwSessionFactory sessionFactory)
    {
        this.sessionName = string.IsNullOrWhiteSpace(sessionName) ? $"StorageChronicle-{Environment.ProcessId}" : sessionName;
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 64);
        this.capacity = capacity;
        this.processLifecycle = processLifecycle;
        this.sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceEvent> CollectAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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
            if (!sessionFactory.IsSupported) yield break;
            session = sessionFactory.Create(sessionName);
            var activeSession = session;
            stop = cancellationToken.Register(activeSession.Stop);
            var processes = new ConcurrentDictionary<int, EtwProcess>();
            var sequence = 0L;
            var overflowed = 0;

            activeSession.ProcessStarted += value => processes[value.ProcessId] = DescribeProcess(value);
            activeSession.ProcessDiscovered += value => processes.TryAdd(value.ProcessId, DescribeProcess(value));
            activeSession.ProcessStopped += value =>
            {
                if (processes.TryRemove(value.ProcessId, out var process))
                    processLifecycle?.RecordProcessExit(process.Id, value.TimeUtc);
            };
            activeSession.FileObserved += value => Enqueue(Create(value, processes, ref sequence), activeSession, queue.Writer, ref overflowed);

            processing = Task.Run(() =>
            {
                try { activeSession.Process(); }
                finally { queue.Writer.TryComplete(); }
            }, CancellationToken.None);

            await foreach (var item in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item;
            await processing.ConfigureAwait(false);
            if (Volatile.Read(ref overflowed) != 0) yield return CreateGap(sequence + 1, "ETW correlation queue exceeded its bound.");
        }
        finally
        {
            session?.Stop();
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

    private static void Enqueue(SourceEvent value, IWindowsEtwSession session, ChannelWriter<SourceEvent> writer, ref int overflowed)
    {
        if (Volatile.Read(ref overflowed) != 0) return;
        if (!writer.TryWrite(value))
        {
            Interlocked.Exchange(ref overflowed, 1);
            session.Stop();
            writer.TryComplete();
        }
    }

    private static SourceEvent Create(EtwFileObservation value, ConcurrentDictionary<int, EtwProcess> processes, ref long sequence)
    {
        var recorded = value.TimeUtc;
        var properties = ImmutableDictionary<string, string>.Empty
            .Add("sourceRoute", "WindowsKernelEtw")
            .Add("path", value.Path);
        if (value.Operation is null) properties = properties.Add("observation", value.Observation ?? "Unknown");
        var process = processes.TryGetValue(value.ProcessId, out var known) ? known : null;
        if (process is not null)
        {
            properties = properties.Add("process.name", process.Name).Add("process.executable", process.Executable);
            if (process.Parent is not null) properties = properties.Add("process.parentInstanceId", process.Parent.Value.Value);
        }

        var eventSequence = Interlocked.Increment(ref sequence);
        return new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.Etw, null, null, null, null, null, value.Operation, null,
            new EventTime(recorded, TimeZoneInfo.Local.GetUtcOffset(recorded), recorded, DateTimeOffset.UtcNow, new SourceSequence(eventSequence), new MountSequence(eventSequence)),
            process is null ? EventQuality.Unknown : EventQuality.Correlated, process?.Id, process is null ? ProcessAttributionQuality.Unknown : ProcessAttributionQuality.Correlated, null, null, properties);
    }

    private static EtwProcess DescribeProcess(EtwProcessStart value)
    {
        var id = ProcessInstanceId.Create($"{value.ProcessId}:{value.TimeUtc.Ticks}");
        ProcessInstanceId? parent = value.ParentProcessId > 0 ? ProcessInstanceId.Create($"{value.ParentProcessId}:unknown") : null;
        return new EtwProcess(id, value.Name, value.Executable, parent);
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

internal interface IWindowsEtwSessionFactory
{
    bool IsSupported { get; }

    IWindowsEtwSession Create(string sessionName);
}

internal interface IWindowsEtwSession : IDisposable
{
    event Action<EtwProcessStart>? ProcessStarted;

    event Action<EtwProcessStart>? ProcessDiscovered;

    event Action<EtwProcessStop>? ProcessStopped;

    event Action<EtwFileObservation>? FileObserved;

    void Process();

    void Stop();
}

internal sealed record EtwProcessStart(int ProcessId, int ParentProcessId, string Name, string Executable, DateTimeOffset TimeUtc);

internal sealed record EtwProcessStop(int ProcessId, DateTimeOffset TimeUtc);

internal sealed record EtwFileObservation(int ProcessId, string Path, CanonicalOperation? Operation, string? Observation, DateTimeOffset TimeUtc);

internal sealed class WindowsEtwSessionFactory : IWindowsEtwSessionFactory
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public IWindowsEtwSession Create(string sessionName) => new WindowsEtwSession(sessionName);

    private sealed class WindowsEtwSession : IWindowsEtwSession
    {
        private readonly TraceEventSession session;

        public WindowsEtwSession(string sessionName)
        {
            session = new TraceEventSession(sessionName) { StopOnDispose = true, CircularBufferMB = 8 };
            try
            {
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.Process);
                var kernel = session.Source.Kernel;
                kernel.ProcessStart += value => ProcessStarted?.Invoke(ToStart(value));
                kernel.ProcessDCStart += value => ProcessDiscovered?.Invoke(ToStart(value));
                kernel.ProcessStop += value => ProcessStopped?.Invoke(new EtwProcessStop(value.ProcessID, new DateTimeOffset(value.TimeStamp.ToUniversalTime())));
                kernel.FileIOFileCreate += value => FileObserved?.Invoke(ToObservation(value, CanonicalOperation.Create, null));
                kernel.FileIOFileDelete += value => FileObserved?.Invoke(ToObservation(value, CanonicalOperation.Delete, null));
                kernel.FileIORename += value => FileObserved?.Invoke(ToObservation(value, CanonicalOperation.Rename, null));
                kernel.FileIOWrite += value => FileObserved?.Invoke(ToObservation(value, CanonicalOperation.DataWrite, null));
                kernel.FileIOSetInfo += value => FileObserved?.Invoke(ToObservation(value, CanonicalOperation.MetadataChanged, null));
                kernel.FileIORead += value => FileObserved?.Invoke(ToObservation(value, null, "Read"));
                kernel.FileIOQueryInfo += value => FileObserved?.Invoke(ToObservation(value, null, "Query"));
                kernel.FileIODirEnum += value => FileObserved?.Invoke(ToObservation(value, null, "DirectoryEnumeration"));
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        public event Action<EtwProcessStart>? ProcessStarted;

        public event Action<EtwProcessStart>? ProcessDiscovered;

        public event Action<EtwProcessStop>? ProcessStopped;

        public event Action<EtwFileObservation>? FileObserved;

        public void Process() => session.Source.Process();

        public void Stop() => session.Stop();

        public void Dispose() => session.Dispose();

        private static EtwProcessStart ToStart(ProcessTraceData value)
        {
            var timestamp = new DateTimeOffset(value.TimeStamp.ToUniversalTime());
            return new EtwProcessStart(value.ProcessID, value.ParentID, value.ImageFileName ?? value.ProcessName ?? "Unknown process", value.KernelImageFileName ?? value.ImageFileName ?? "Unknown executable", timestamp);
        }

        private static EtwFileObservation ToObservation(TraceEvent value, CanonicalOperation? operation, string? observation)
        {
            var path = value is FileIONameTraceData named ? named.FileName : value.PayloadString(0, System.Globalization.CultureInfo.InvariantCulture);
            return new EtwFileObservation(value.ProcessID, path ?? string.Empty, operation, observation, new DateTimeOffset(value.TimeStamp.ToUniversalTime()));
        }
    }
}
