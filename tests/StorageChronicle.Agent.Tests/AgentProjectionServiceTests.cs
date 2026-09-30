using System.Collections.Immutable;
using StorageChronicle.Agent;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Settings;
using StorageChronicle.Storage;
using StorageChronicle.UI.Shared;
using Xunit;

namespace StorageChronicle.Agent.Tests;

public sealed class AgentProjectionServiceTests
{
    [Fact]
    public void ProcessLifecycleRegistryKeepsEarliestDuplicateAndRejectsInvalidObservations()
    {
        var state = new AgentProcessLifecycleState();
        var processId = ProcessInstanceId.Create("process-lifecycle-duplicate");
        var first = DateTimeOffset.UtcNow;
        state.RecordProcessExit(processId, first.AddSeconds(1));
        state.RecordProcessExit(processId, first);

        Assert.True(state.TryGetProcessExit(processId, out var actual));
        Assert.Equal(first, actual);
        Assert.Throws<ArgumentException>(() => state.RecordProcessExit(default, first));
        Assert.Throws<ArgumentException>(() => state.RecordProcessExit(ProcessInstanceId.Create("process-lifecycle-default-time"), default));
    }

    [Fact]
    public void ProcessLifecycleRegistryEvictsOldestEntryAtCapacity()
    {
        var state = new AgentProcessLifecycleState();
        var start = DateTimeOffset.UtcNow;
        for (var index = 0; index < 8193; index++)
        {
            state.RecordProcessExit(ProcessInstanceId.Create($"process-lifecycle-{index}"), start.AddTicks(index + 1));
        }

        Assert.False(state.TryGetProcessExit(ProcessInstanceId.Create("process-lifecycle-0"), out _));
        Assert.True(state.TryGetProcessExit(ProcessInstanceId.Create("process-lifecycle-8192"), out _));
    }

    [Fact]
    public async Task DiffProjectionReturnsConcurrentActivityFramesAndTimelinePages()
    {
        var fixtureRoot = AgentTestFixtureOwnership.CreateTempRoot("StorageChronicle.ActivityFrames", out var runId);
        var historyRoot = Path.Combine(fixtureRoot, "history");
        try
        {
            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(historyRoot) { FlushInterval = TimeSpan.FromMinutes(1) });
            var start = DateTimeOffset.UtcNow.AddMinutes(-1);
            var first = Event(start, 1, "process-1", "C:\\data\\a.txt");
            var concurrent = Event(start.AddSeconds(1), 2, "process-2", "D:\\other\\b.txt");
            var second = Event(start.AddSeconds(2), 3, "process-1", "C:\\data\\a.txt");
            await storage.AppendCanonicalAsync(first);
            await storage.AppendCanonicalAsync(concurrent);
            await storage.AppendCanonicalAsync(second);

            var service = new AgentProjectionService(storage, userSettingsStore: new FixedUserSettingsStore(new UserSettings { PaneTimeoutSeconds = 5 }));
            var response = await service.GetDiffProjectionAsync(new DiffProjectionRequest(start, start.AddSeconds(3), DiffMode.Live, ActivityFramesPageSize: 1, ActivityFramesAscending: true));

            Assert.Equal(2, response.ActivityFramesTotalCount);
            Assert.True(response.HasMoreActivityFrames);
            var frame = Assert.Single(response.ActivityFrames!);
            Assert.Equal(first.EventId.ToString(), frame.FrameId);
            Assert.Equal(2, frame.OperationCount);
            Assert.Equal(start.AddSeconds(7), frame.CloseBoundaryUtc);

            var timelineFirstPage = await service.GetActivityFrameTimelineAsync(new DiffActivityFrameTimelineRequest(frame.FrameId, start, start.AddSeconds(3), PageSize: 1));
            Assert.Equal(2, timelineFirstPage.TotalCount);
            Assert.True(timelineFirstPage.HasMore);
            Assert.Equal(first.EventId, Assert.Single(timelineFirstPage.Events).EventId);

            var timelineSecondPage = await service.GetActivityFrameTimelineAsync(new DiffActivityFrameTimelineRequest(frame.FrameId, start, start.AddSeconds(3), Page: 2, PageSize: 1));
            Assert.Equal(second.EventId, Assert.Single(timelineSecondPage.Events).EventId);

            var expired = await service.GetDiffProjectionAsync(new DiffProjectionRequest(start, start.AddSeconds(8), DiffMode.Live));
            Assert.Empty(expired.ActivityFrames!);
            Assert.Equal(0, expired.ActivityFramesTotalCount);
        }
        finally
        {
            AgentTestFixtureOwnership.DeleteTempRoot(fixtureRoot, "StorageChronicle.ActivityFrames", runId);
        }
    }

    [Fact]
    public async Task ActivityFrameServerPagingHonorsSortDirectionBeforeSelectingPage()
    {
        var fixtureRoot = AgentTestFixtureOwnership.CreateTempRoot("StorageChronicle.ActivityFramesOrder", out var runId);
        try
        {
            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(Path.Combine(fixtureRoot, "history")) { FlushInterval = TimeSpan.FromMinutes(1) });
            var start = DateTimeOffset.UtcNow.AddMinutes(-1);
            var older = Event(start, 1, "process-1", "C:\\data\\older.txt");
            var newer = Event(start.AddSeconds(1), 2, "process-2", "D:\\other\\newer.txt");
            await storage.AppendCanonicalAsync(older);
            await storage.AppendCanonicalAsync(newer);
            var service = new AgentProjectionService(storage);

            var newestFirst = await service.GetDiffProjectionAsync(new DiffProjectionRequest(start, start.AddSeconds(2), DiffMode.Live, ActivityFramesPageSize: 1));
            var oldestFirst = await service.GetDiffProjectionAsync(new DiffProjectionRequest(start, start.AddSeconds(2), DiffMode.Live, ActivityFramesPageSize: 1, ActivityFramesAscending: true));

            Assert.Equal(newer.EventId.ToString(), Assert.Single(newestFirst.ActivityFrames!).FrameId);
            Assert.Equal(older.EventId.ToString(), Assert.Single(oldestFirst.ActivityFrames!).FrameId);
        }
        finally
        {
            AgentTestFixtureOwnership.DeleteTempRoot(fixtureRoot, "StorageChronicle.ActivityFramesOrder", runId);
        }
    }

    [Fact]
    public async Task PointInTimeProjectionDoesNotReturnLiveActivityFrames()
    {
        var fixtureRoot = AgentTestFixtureOwnership.CreateTempRoot("StorageChronicle.ActivityFrames", out var runId);
        try
        {
            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(Path.Combine(fixtureRoot, "history")) { FlushInterval = TimeSpan.FromMinutes(1) });
            var now = DateTimeOffset.UtcNow;
            await storage.AppendCanonicalAsync(Event(now, 1, "process-1", "C:\\data\\a.txt"));
            var service = new AgentProjectionService(storage);

            var response = await service.GetDiffProjectionAsync(new DiffProjectionRequest(now, now, DiffMode.PointInTime));

            Assert.Empty(response.ActivityFrames!);
            Assert.Equal(0, response.ActivityFramesTotalCount);
        }
        finally
        {
            AgentTestFixtureOwnership.DeleteTempRoot(fixtureRoot, "StorageChronicle.ActivityFrames", runId);
        }
    }

    [Fact]
    public async Task ActivityFrameProjectionUsesDefaultTimeoutAndObservesCancellation()
    {
        var fixtureRoot = AgentTestFixtureOwnership.CreateTempRoot("StorageChronicle.ActivityFrames", out var runId);
        try
        {
            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(Path.Combine(fixtureRoot, "history")) { FlushInterval = TimeSpan.FromMinutes(1) });
            var now = DateTimeOffset.UtcNow;
            await storage.AppendCanonicalAsync(Event(now, 1, "process-1", "C:\\data\\a.txt"));
            var service = new AgentProjectionService(storage);
            var live = await service.GetDiffProjectionAsync(new DiffProjectionRequest(now, now.AddSeconds(1), DiffMode.Live));
            Assert.Equal(1, live.ActivityFramesTotalCount);
            Assert.Equal(now.AddSeconds(5), Assert.Single(live.ActivityFrames!).CloseBoundaryUtc);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await service.GetActivityFrameTimelineAsync(new DiffActivityFrameTimelineRequest(live.ActivityFrames![0].FrameId, now, now.AddSeconds(1)), cancelled.Token));
        }
        finally
        {
            AgentTestFixtureOwnership.DeleteTempRoot(fixtureRoot, "StorageChronicle.ActivityFrames", runId);
        }
    }

    [Fact]
    public async Task DesktopPipeClientReadsActivityFrameSummaryAndTimeline()
    {
        var fixtureRoot = AgentTestFixtureOwnership.CreateTempRoot("StorageChronicle.ActivityFramesPipe", out var runId);
        try
        {
            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(Path.Combine(fixtureRoot, "history")) { FlushInterval = TimeSpan.FromMinutes(1) });
            var now = DateTimeOffset.UtcNow;
            var value = Event(now, 1, "process-1", "C:\\data\\pipe.txt");
            await storage.AppendCanonicalAsync(value);
            using var server = new NamedPipeAgentServer(new AgentProjectionService(storage), storage, new AgentHealthState(), new StorageChronicle.Normalization.EventNormalizer());
            using var stopped = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.StartAsync(stopped.Token);

            var client = new AgentPipeProjectionClient();
            var diff = await client.GetDiffProjectionSnapshotAsync(new DiffProjectionRequest(now, now.AddSeconds(1), DiffMode.Live), stopped.Token);
            var frame = Assert.Single(diff.ActivityFrames!);
            var timeline = await client.GetActivityFrameTimelineAsync(new DiffActivityFrameTimelineRequest(frame.FrameId, now, now.AddSeconds(1)), stopped.Token);

            Assert.Equal(value.EventId, Assert.Single(timeline.Events).EventId);
            await server.StopAsync(CancellationToken.None);
        }
        finally
        {
            AgentTestFixtureOwnership.DeleteTempRoot(fixtureRoot, "StorageChronicle.ActivityFramesPipe", runId);
        }
    }

    [Fact]
    public async Task ObservedProcessExitClosesLiveFrameEarlierThanPaneTimeout()
    {
        var fixtureRoot = AgentTestFixtureOwnership.CreateTempRoot("StorageChronicle.ActivityFrames", out var runId);
        try
        {
            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(Path.Combine(fixtureRoot, "history")) { FlushInterval = TimeSpan.FromMinutes(1) });
            var now = DateTimeOffset.UtcNow;
            var value = Event(now, 1, "process-1", "C:\\data\\exit.txt");
            await storage.AppendCanonicalAsync(value);
            var lifecycle = new FixedProcessLifecycleSink(ProcessInstanceId.Create("process-1"), now.AddSeconds(2));
            var service = new AgentProjectionService(storage, processLifecycle: lifecycle);

            var beforeExit = await service.GetDiffProjectionAsync(new DiffProjectionRequest(now, now.AddSeconds(1), DiffMode.Live));
            var frame = Assert.Single(beforeExit.ActivityFrames!);
            Assert.True(frame.IsClosedByProcessExit);
            Assert.Equal(now.AddSeconds(2), frame.CloseBoundaryUtc);
            var afterExit = await service.GetDiffProjectionAsync(new DiffProjectionRequest(now, now.AddSeconds(3), DiffMode.Live));
            Assert.Empty(afterExit.ActivityFrames!);
        }
        finally
        {
            AgentTestFixtureOwnership.DeleteTempRoot(fixtureRoot, "StorageChronicle.ActivityFrames", runId);
        }
    }

    private static CanonicalEvent Event(DateTimeOffset recordedUtc, long sequence, string process, string path)
    {
        var name = Path.GetFileName(path);
        var time = new EventTime(recordedUtc, TimeSpan.Zero, null, recordedUtc, new SourceSequence(sequence), new MountSequence(sequence));
        return new CanonicalEvent(
            EventId.New(), EventSchemaVersion.Current, CanonicalOperation.DataWrite, EventOrigin.Etw,
            VolumeId.Create("activity-frame-test-volume"), FileId.Create(path), null, name, null, null, time,
            EventQuality.Exact, ProcessInstanceId.Create(process), ProcessAttributionQuality.Exact, null, null,
            ImmutableDictionary<string, string>.Empty.Add("path", path).Add("process.name", process + ".exe"));
    }

    private sealed class FixedUserSettingsStore(UserSettings settings) : ISettingsStore<UserSettings>
    {
        public SettingsLoadResult<UserSettings> Load() => new(settings, false, false, null);
        public void Save(UserSettings value) => throw new NotSupportedException();
    }

    private sealed class FixedProcessLifecycleSink : StorageChronicle.Platform.Abstractions.IProcessLifecycleSink
    {
        private readonly Dictionary<ProcessInstanceId, DateTimeOffset> exits;

        public FixedProcessLifecycleSink(ProcessInstanceId processId, DateTimeOffset exitedUtc) => exits = new() { [processId] = exitedUtc };

        public void RecordProcessExit(ProcessInstanceId processInstanceId, DateTimeOffset exitTime) => exits[processInstanceId] = exitTime;

        public bool TryGetProcessExit(ProcessInstanceId processInstanceId, out DateTimeOffset exitTime) => exits.TryGetValue(processInstanceId, out exitTime);
    }
}
