using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Abstractions;
using Xunit;

namespace StorageChronicle.Platform.Windows.Ntfs.Tests;

public sealed class WindowsEtwFileIoCollectorTests
{
    [Fact]
    public async Task MapsOperationsAndReadObservationsAndReportsProcessExit()
    {
        var session = new FakeSession
        {
            OnProcess = current =>
            {
                var start = new DateTimeOffset(2026, 9, 30, 1, 2, 3, TimeSpan.Zero);
                current.RaiseProcessDiscovered(new EtwProcessStart(42, 7, "writer.exe", "C:\\tools\\writer.exe", start));
                current.RaiseFileObserved(new EtwFileObservation(42, "C:\\fixture\\a.txt", CanonicalOperation.Create, null, start.AddSeconds(1)));
                current.RaiseFileObserved(new EtwFileObservation(42, "C:\\fixture\\a.txt", null, "Read", start.AddSeconds(2)));
                current.RaiseProcessStopped(new EtwProcessStop(42, start.AddSeconds(3)));
            }
        };
        var lifecycle = new RecordingLifecycleSink();
        var collector = CreateCollector(new FakeFactory(session), lifecycle: lifecycle);

        var events = await CollectAsync(collector, TestContext.Current.CancellationToken);

        var write = Assert.Single(events, item => item.Hint == CanonicalOperation.Create);
        Assert.Equal("C:\\fixture\\a.txt", write.Properties["path"]);
        Assert.Equal(ProcessAttributionQuality.Correlated, write.ProcessQuality);
        Assert.Equal("writer.exe", write.Properties["process.name"]);
        var read = Assert.Single(events, item => item.Properties.GetValueOrDefault("observation") == "Read");
        Assert.Null(read.Hint);
        Assert.Equal(ProcessAttributionQuality.Correlated, read.ProcessQuality);
        var exit = Assert.Single(lifecycle.Exits);
        Assert.Equal(write.ProcessInstanceId, exit.ProcessInstanceId);
    }

    [Fact]
    public async Task MissingProcessMappingRemainsUnknown()
    {
        var session = new FakeSession
        {
            OnProcess = current => current.RaiseFileObserved(new EtwFileObservation(99, "C:\\fixture\\unknown.txt", CanonicalOperation.DataWrite, null, DateTimeOffset.UtcNow))
        };

        var events = await CollectAsync(CreateCollector(new FakeFactory(session)), TestContext.Current.CancellationToken);

        var item = Assert.Single(events);
        Assert.Equal(ProcessAttributionQuality.Unknown, item.ProcessQuality);
        Assert.Null(item.ProcessInstanceId);
    }

    [Fact]
    public async Task BoundedQueueOverflowStopsSessionAndEmitsGapAfterQueuedEvents()
    {
        var session = new FakeSession
        {
            WaitUntilStopped = true,
            OnProcess = current => current.RaiseFileObserved(new EtwFileObservation(1, "C:\\fixture\\initial.txt", CanonicalOperation.Create, null, DateTimeOffset.UtcNow))
        };
        var collector = CreateCollector(new FakeFactory(session), capacity: 64);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var enumerator = collector.CollectAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await enumerator.MoveNextAsync());

        for (var index = 0; index < 1_000; index++)
        {
            session.RaiseFileObserved(new EtwFileObservation(1, $"C:\\fixture\\{index}.txt", CanonicalOperation.Create, null, DateTimeOffset.UtcNow));
        }

        Assert.True(session.StopCalled);
        var remaining = new List<SourceEvent>();
        while (await enumerator.MoveNextAsync()) remaining.Add(enumerator.Current);

        Assert.Contains(remaining, item => item.Hint == CanonicalOperation.UnverifiedGap && item.Quality == EventQuality.UnverifiedGap);
        Assert.True(remaining.Count <= 65);
    }

    [Fact]
    public async Task CancellationStopsAndDisposesSession()
    {
        var session = new FakeSession { WaitUntilStopped = true };
        var collector = CreateCollector(new FakeFactory(session));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var enumerator = collector.CollectAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var pending = enumerator.MoveNextAsync().AsTask();
        await session.ProcessStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(session.StopCalled);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task SessionStartupFailurePropagatesWithoutOpeningAnyHostSession()
    {
        var collector = CreateCollector(new FakeFactory(createFailure: new InvalidOperationException("synthetic startup failure")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(collector, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SessionProcessingFailurePropagatesToTheCaller()
    {
        var session = new FakeSession { OnProcess = _ => throw new IOException("synthetic processing failure") };
        var collector = CreateCollector(new FakeFactory(session));

        await Assert.ThrowsAsync<IOException>(() => CollectAsync(collector, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnsupportedPlatformEndsWithoutCreatingSession()
    {
        var factory = new FakeFactory(new FakeSession(), isSupported: false);
        var events = await CollectAsync(CreateCollector(factory), TestContext.Current.CancellationToken);

        Assert.Empty(events);
        Assert.Equal(0, factory.CreateCount);
    }

    private static WindowsEtwFileIoCollector CreateCollector(FakeFactory factory, int capacity = 64, IProcessLifecycleSink? lifecycle = null) =>
        new("test-session", capacity, lifecycle, factory);

    private static async Task<List<SourceEvent>> CollectAsync(WindowsEtwFileIoCollector collector, CancellationToken cancellationToken)
    {
        var events = new List<SourceEvent>();
        await foreach (var item in collector.CollectAsync(cancellationToken).WithCancellation(cancellationToken)) events.Add(item);
        return events;
    }

    private sealed class FakeFactory(FakeSession? session = null, Exception? createFailure = null, bool isSupported = true) : IWindowsEtwSessionFactory
    {
        public bool IsSupported { get; } = isSupported;

        public int CreateCount { get; private set; }

        public IWindowsEtwSession Create(string sessionName)
        {
            CreateCount++;
            if (createFailure is not null) throw createFailure;
            return session ?? new FakeSession();
        }
    }

    private sealed class FakeSession : IWindowsEtwSession
    {
        private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<FakeSession>? OnProcess { get; init; }

        public bool WaitUntilStopped { get; init; }

        private int stopCalled;

        private int disposed;

        public bool StopCalled => Volatile.Read(ref stopCalled) != 0;

        public bool Disposed => Volatile.Read(ref disposed) != 0;

        public TaskCompletionSource ProcessStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<EtwProcessStart>? ProcessStartedEvent;

        public event Action<EtwProcessStart>? ProcessDiscovered;

        public event Action<EtwProcessStop>? ProcessStopped;

        public event Action<EtwFileObservation>? FileObserved;

        event Action<EtwProcessStart>? IWindowsEtwSession.ProcessStarted
        {
            add => ProcessStartedEvent += value;
            remove => ProcessStartedEvent -= value;
        }

        public void Process()
        {
            ProcessStarted.TrySetResult();
            OnProcess?.Invoke(this);
            if (WaitUntilStopped) stopped.Task.GetAwaiter().GetResult();
        }

        public void Stop()
        {
            Interlocked.Exchange(ref stopCalled, 1);
            stopped.TrySetResult();
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref disposed, 1);
            Stop();
        }

        public void RaiseProcessStarted(EtwProcessStart value) => ProcessStartedEvent?.Invoke(value);

        public void RaiseProcessDiscovered(EtwProcessStart value) => ProcessDiscovered?.Invoke(value);

        public void RaiseFileObserved(EtwFileObservation value) => FileObserved?.Invoke(value);

        public void RaiseProcessStopped(EtwProcessStop value) => ProcessStopped?.Invoke(value);
    }

    private sealed class RecordingLifecycleSink : IProcessLifecycleSink
    {
        public List<(ProcessInstanceId ProcessInstanceId, DateTimeOffset ExitedUtc)> Exits { get; } = [];

        public void RecordProcessExit(ProcessInstanceId processInstanceId, DateTimeOffset exitedUtc) => Exits.Add((processInstanceId, exitedUtc));

        public bool TryGetProcessExit(ProcessInstanceId processInstanceId, out DateTimeOffset exitedUtc)
        {
            var match = Exits.LastOrDefault(item => item.ProcessInstanceId == processInstanceId);
            exitedUtc = match.ExitedUtc;
            return match.ProcessInstanceId == processInstanceId;
        }
    }
}
