using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using StorageChronicle.Agent;
using StorageChronicle.Contracts;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.ExternalMedia;
using StorageChronicle.Platform.Windows.FileSystem;
using StorageChronicle.Settings;
using StorageChronicle.Storage;
using Xunit;

namespace StorageChronicle.Agent.Tests;

public sealed class MediaConsentLifecycleIntegrationTests
{
    private const string ApproverSid = "S-1-5-21-100-200-300-1001";
    private static readonly string AclFingerprint = new('B', 64);

    [Fact]
    public async Task PendingApprovalIsPersistedBeforeCollectorImportsHistoryAndCoordinatorAppends()
    {
        using var fixture = new ConsentLifecycleFixture();
        var priorEvent = Event(fixture.Media.LogicalMediaId, fixture.VolumeId, "source-mount", 1);
        using (var sourceStore = new ExternalMediaStore(fixture.MediaRoot, "source-pc", fixture.VolumeId, fixture.FileSystems.Open(fixture.VolumeId), writeAuthorization: _ => true))
        {
            var segment = await sourceStore.AppendSegmentAsync([priorEvent], TestContext.Current.CancellationToken);
            await sourceStore.PublishManifestAsync(fixture.Media.LogicalMediaId, null, "source-mount", [segment], TestContext.Current.CancellationToken);
        }

        var coordinator = new ExternalMediaMirrorCoordinator(fixture.MachineStore, "test-pc", new TimelineExclusionRegistrar(fixture.Timeline), fixture.FileSystems, fixture.Consent);
        var changes = new OneChangeSource(new ExternalMediaChange(ExternalMediaChangeKind.Connected, DateTimeOffset.UtcNow, "fixture-connect"));
        var collector = new WindowsExternalMediaCollector(
            changes,
            new FixedVolumeEnumerator(fixture.Descriptor),
            fixture.MachineStore,
            "test-pc",
            path => new MediaImportLedgerStore(Path.Combine(fixture.LedgerRoot, Path.GetFileName(path)), isolatedTestRoot: true),
            mirrorCoordinator: coordinator,
            fileSystemFactory: fixture.FileSystems,
            consent: fixture.Consent);
        var collected = new List<SourceEvent>();
        using var collectCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var collectTask = Task.Run(async () =>
        {
            await foreach (var item in collector.CollectAsync(collectCancellation.Token)) collected.Add(item);
        }, collectCancellation.Token);

        try
        {
            var request = await fixture.WaitForApprovalAsync();
            Assert.True(request.ExistingHistoryReadAndImportRequested);
            Assert.Empty(Directory.EnumerateFiles(fixture.LedgerRoot));
            Assert.DoesNotContain(fixture.Timeline, item => item is "settings-saved" or "settings-history" or "coordinator-exclusion");

            Assert.True(await fixture.Consent.DecideAsync(new MediaMirrorApprovalDecision(request.RequestId, Approve: true), ApproverSid, TestContext.Current.CancellationToken));
            await collectTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Contains(Directory.EnumerateFiles(fixture.LedgerRoot), path => path.EndsWith(".generation.json", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(collected, item => item.Properties.TryGetValue("media.quality", out var quality) && quality == "MirroredFromAnotherPc");
            Assert.True(fixture.Timeline.IndexOf("settings-saved") < fixture.Timeline.IndexOf("settings-history"));
            Assert.True(fixture.Timeline.IndexOf("settings-history") < fixture.Timeline.IndexOf("coordinator-exclusion"));

            var appended = Event(fixture.Media.LogicalMediaId, fixture.VolumeId, "current-mount", 2);
            await coordinator.OnCanonicalAsync(appended, TestContext.Current.CancellationToken);
            await coordinator.FlushAsync(TestContext.Current.CancellationToken);

            using var reader = new ExternalMediaStore(fixture.MediaRoot, "test-pc", fixture.VolumeId, fixture.FileSystems.Open(fixture.VolumeId), createIfMissing: false);
            var currentWriter = Assert.Single(reader.OpenWriterStores(), store => store.WriterPcId == "test-pc");
            var manifests = await currentWriter.ReadManifestCandidatesAsync(TestContext.Current.CancellationToken);
            Assert.Contains(manifests.SelectMany(manifest => manifest.Segments), value => value.RecordCount == 1);
            Assert.Single(fixture.MachineStore.Load().Settings.MediaMirrorConsents);
            Assert.True(Assert.Single(fixture.MachineStore.Load().Settings.MediaMirrorConsents).ExistingHistoryReadAndImportAllowed);
        }
        finally
        {
            if (!collectTask.IsCompleted) collectCancellation.Cancel();
            try { await collectTask; }
            catch (OperationCanceledException) when (collectCancellation.IsCancellationRequested) { }
            finally
            {
                try { await collector.DisposeAsync(); }
                finally { await coordinator.DisposeAsync(); }
            }
        }
    }

    [Fact]
    public async Task CoordinatorRejectsMirrorRegistrationUntilExactPersistedBindingExists()
    {
        using var fixture = new ConsentLifecycleFixture();
        var coordinator = new ExternalMediaMirrorCoordinator(fixture.MachineStore, "test-pc", new TimelineExclusionRegistrar(fixture.Timeline), fixture.FileSystems, fixture.Consent);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await coordinator.RegisterAsync(fixture.Media, MountSessionTrackerSession(fixture.VolumeId)));
            Assert.DoesNotContain("coordinator-exclusion", fixture.Timeline);

            var approval = fixture.Consent.AuthorizeAsync(fixture.Media, fixture.MediaRoot).AsTask();
            var request = await fixture.WaitForApprovalAsync();
            Assert.True(await fixture.Consent.DecideAsync(new MediaMirrorApprovalDecision(request.RequestId, Approve: true), ApproverSid, TestContext.Current.CancellationToken));
            Assert.True(await approval);

            await coordinator.RegisterAsync(fixture.Media, MountSessionTrackerSession(fixture.VolumeId), TestContext.Current.CancellationToken);
            Assert.Contains("coordinator-exclusion", fixture.Timeline);
            Assert.Single(fixture.MachineStore.Load().Settings.MediaMirrorConsents);
        }
        finally { await coordinator.DisposeAsync(); }
    }

    [Fact]
    public async Task CoordinatorRechecksLiveAclBeforeFlushAndRetainsPendingEventsOnDenial()
    {
        using var fixture = new ConsentLifecycleFixture();
        var approval = fixture.Consent.AuthorizeAsync(fixture.Media, fixture.MediaRoot).AsTask();
        var request = await fixture.WaitForApprovalAsync();
        Assert.True(await fixture.Consent.DecideAsync(new MediaMirrorApprovalDecision(request.RequestId, Approve: true), ApproverSid, TestContext.Current.CancellationToken));
        Assert.True(await approval);

        var coordinator = new ExternalMediaMirrorCoordinator(fixture.MachineStore, "test-pc", new TimelineExclusionRegistrar(fixture.Timeline), fixture.FileSystems, fixture.Consent);
        try
        {
            await coordinator.RegisterAsync(fixture.Media, MountSessionTrackerSession(fixture.VolumeId), TestContext.Current.CancellationToken);
            var writerDirectory = Path.Combine(fixture.MediaRoot, ".StorageChronicle", "writers", "test-pc");
            var entriesBeforeDeniedFlush = Directory.EnumerateFileSystemEntries(writerDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            var value = Event(fixture.Media.LogicalMediaId, fixture.VolumeId, "current-mount", 10);
            await coordinator.OnCanonicalAsync(value, TestContext.Current.CancellationToken);

            fixture.CurrentAclStatus = MediaMirrorAclInspectionStatus.Unsafe;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await coordinator.FlushAsync(TestContext.Current.CancellationToken));
            Assert.Equal(entriesBeforeDeniedFlush, Directory.EnumerateFileSystemEntries(writerDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());

            fixture.CurrentAclStatus = MediaMirrorAclInspectionStatus.Verified;
            await coordinator.FlushAsync(TestContext.Current.CancellationToken);

            using var reader = new ExternalMediaStore(fixture.MediaRoot, "test-pc", fixture.VolumeId, fixture.FileSystems.Open(fixture.VolumeId), createIfMissing: false);
            var currentWriter = Assert.Single(reader.OpenWriterStores(), store => store.WriterPcId == "test-pc");
            var manifests = await currentWriter.ReadManifestCandidatesAsync(TestContext.Current.CancellationToken);
            Assert.Contains(manifests.SelectMany(manifest => manifest.Segments), segment => segment.RecordCount == 1);
        }
        finally { await coordinator.DisposeAsync(); }
    }

    [Fact]
    public async Task CoordinatorRetainsFinalizedSegmentUntilManifestCanBePublished()
    {
        using var fixture = new ConsentLifecycleFixture();
        var approval = fixture.Consent.AuthorizeAsync(fixture.Media, fixture.MediaRoot).AsTask();
        var request = await fixture.WaitForApprovalAsync();
        Assert.True(await fixture.Consent.DecideAsync(new MediaMirrorApprovalDecision(request.RequestId, Approve: true), ApproverSid, TestContext.Current.CancellationToken));
        Assert.True(await approval);

        var coordinator = new ExternalMediaMirrorCoordinator(fixture.MachineStore, "test-pc", new TimelineExclusionRegistrar(fixture.Timeline), fixture.FileSystems, fixture.Consent);
        try
        {
            await coordinator.RegisterAsync(fixture.Media, MountSessionTrackerSession(fixture.VolumeId), TestContext.Current.CancellationToken);
            var value = Event(fixture.Media.LogicalMediaId, fixture.VolumeId, "current-mount", 11);
            await coordinator.OnCanonicalAsync(value, TestContext.Current.CancellationToken);
            fixture.UnsafeAclInspectionNumber = fixture.AclInspectionCount + 4;

            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await coordinator.FlushAsync(TestContext.Current.CancellationToken));
            var writerDirectory = Path.Combine(fixture.MediaRoot, ".StorageChronicle", "writers", "test-pc");
            Assert.Single(Directory.EnumerateFiles(writerDirectory), path => path.EndsWith(".seg", StringComparison.OrdinalIgnoreCase));
            using (var reader = new ExternalMediaStore(fixture.MediaRoot, "test-pc", fixture.VolumeId, fixture.FileSystems.Open(fixture.VolumeId), createIfMissing: false))
            {
                var currentWriter = Assert.Single(reader.OpenWriterStores(), store => store.WriterPcId == "test-pc");
                Assert.Empty(await currentWriter.ReadManifestCandidatesAsync(TestContext.Current.CancellationToken));
            }

            fixture.CurrentAclStatus = MediaMirrorAclInspectionStatus.Unsafe;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await coordinator.UnregisterAsync(fixture.VolumeId, TestContext.Current.CancellationToken));
            fixture.CurrentAclStatus = MediaMirrorAclInspectionStatus.Verified;
            await coordinator.UnregisterAsync(fixture.VolumeId, TestContext.Current.CancellationToken);

            using var finalReader = new ExternalMediaStore(fixture.MediaRoot, "test-pc", fixture.VolumeId, fixture.FileSystems.Open(fixture.VolumeId), createIfMissing: false);
            var finalWriter = Assert.Single(finalReader.OpenWriterStores(), store => store.WriterPcId == "test-pc");
            var manifests = await finalWriter.ReadManifestCandidatesAsync(TestContext.Current.CancellationToken);
            Assert.Contains(manifests.SelectMany(manifest => manifest.Segments), segment => segment.RecordCount == 1);
        }
        finally { await coordinator.DisposeAsync(); }
    }

    private static MountSession MountSessionTrackerSession(VolumeId volumeId) =>
        new MountSessionTracker().Start(volumeId, "test-pc", MonitoringContinuity.Continuous);

    private static CanonicalEvent Event(string mediaId, VolumeId volumeId, string mountId, long sequence)
    {
        var now = DateTimeOffset.UtcNow;
        return new CanonicalEvent(EventId.New(), EventSchemaVersion.Current, CanonicalOperation.Create, EventOrigin.LiveUsn,
            volumeId, FileId.Create(Guid.NewGuid().ToString("N")), null, "fixture.txt", null, null,
            new EventTime(now, TimeSpan.Zero, null, now, new SourceSequence(sequence), new MountSequence(sequence)),
            EventQuality.Exact, null, ProcessAttributionQuality.Unknown, MountSessionId.Create(mountId), null,
            ImmutableDictionary<string, string>.Empty.Add("media.logicalMediaId", mediaId).Add("media.source", "external"));
    }

    private sealed class ConsentLifecycleFixture : IDisposable
    {
        private readonly string root;
        private readonly string runId;
        private readonly AgentSettingsService settingsService;
        private int aclInspectionCount;
        private readonly AgentHealthState health = new();
        private readonly FixedVolumeEnumerator volumes;

        public ConsentLifecycleFixture()
        {
            root = AgentTestFixtureOwnership.CreateTempRoot("StorageChronicle.Media.Tests", out runId);
            MediaRoot = Path.Combine(root, "media");
            LedgerRoot = Path.Combine(root, "ledger");
            Directory.CreateDirectory(MediaRoot);
            Directory.CreateDirectory(LedgerRoot);
            VolumeId = VolumeId.Create("consent-lifecycle-volume");
            Descriptor = new VolumeDescriptor(VolumeId, "NTFS", [MediaRoot], false, true, ProtectedVolumeRoles.None, true, true, IsProtectedRoleClassificationComplete: true);
            Media = new MediaVolumeDescriptor(VolumeId.Value, VolumeId, "NTFS", false, true, ProtectedVolumeRoles.None, true) { MountPoints = [MediaRoot] };
            MachineStore = new TimelineSettingsStore<MachineSettings>(new MachineSettings
            {
                LogStoragePath = Path.Combine(root, "local-history"),
                MediaMirrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Media.LogicalMediaId] = MediaRoot }
            }, Timeline);
            FileSystems = new FixtureVolumeFileSystemFactory(MediaRoot, () => "fixture-owned-root-identity", sid =>
            {
                if (sid != ApproverSid) return new(MediaMirrorAclInspectionStatus.Unknown, null, 0, 0, ["WrongApproverSid"]);
                var status = Interlocked.Increment(ref aclInspectionCount) == UnsafeAclInspectionNumber
                    ? MediaMirrorAclInspectionStatus.Unsafe
                    : CurrentAclStatus;
                return new(status, status == MediaMirrorAclInspectionStatus.Verified ? AclFingerprint : null, 1, 0,
                    status == MediaMirrorAclInspectionStatus.Verified ? [] : ["FixtureAclUnsafe"]);
            });
            using (var initialized = new ExternalMediaStore(MediaRoot, "test-pc", VolumeId, FileSystems.Open(VolumeId), writeAuthorization: _ => true)) { }
            volumes = new FixedVolumeEnumerator(Descriptor);
            settingsService = new AgentSettingsService(MachineStore, new FixedSettingsStore<UserSettings>(new UserSettings()), new TimelineHistory(Timeline), new AllowAllAgentSettingsAuthorizer(), new NoOpLifecycle());
            Consent = new MediaMirrorConsentService(health, MachineStore, settingsService, volumes, FileSystems, "test-pc");
        }

        public string MediaRoot { get; }
        public string LedgerRoot { get; }
        public VolumeId VolumeId { get; }
        public VolumeDescriptor Descriptor { get; }
        public MediaVolumeDescriptor Media { get; }
        public IVolumeBoundMediaFileSystemFactory FileSystems { get; }
        public TimelineSettingsStore<MachineSettings> MachineStore { get; }
        public MediaMirrorConsentService Consent { get; }
        public List<string> Timeline { get; } = [];
        public MediaMirrorAclInspectionStatus CurrentAclStatus { get; set; } = MediaMirrorAclInspectionStatus.Verified;
        public int UnsafeAclInspectionNumber { get; set; } = -1;
        public int AclInspectionCount => Volatile.Read(ref aclInspectionCount);

        public async Task<PendingMediaMirrorApproval> WaitForApprovalAsync()
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var requests = health.Snapshot(new RecordingStatus(RecordingState.Running, 0, 0, null)).PendingMediaMirrorApprovals;
                if (requests is { Count: > 0 }) return requests[0];
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException("The collector did not publish its explicit media-consent approval request.");
        }

        public void Dispose()
        {
            settingsService.Dispose();
            AgentTestFixtureOwnership.DeleteTempRoot(root, "StorageChronicle.Media.Tests", runId);
        }
    }

    private sealed class TimelineSettingsStore<T>(T settings, List<string> timeline) : ISettingsStore<T> where T : class
    {
        private T value = settings;
        public SettingsLoadResult<T> Load() => new(value, false, false, null);
        public void Save(T settings)
        {
            value = settings;
            timeline.Add("settings-saved");
        }
    }

    private sealed class FixedSettingsStore<T>(T settings) : ISettingsStore<T> where T : class
    {
        public SettingsLoadResult<T> Load() => new(settings, false, false, null);
        public void Save(T value) => throw new NotSupportedException();
    }

    private sealed class TimelineHistory(List<string> timeline) : ISettingsChangeHistory
    {
        public ValueTask RecordAsync(SettingsChangeHistoryEvent value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            timeline.Add("settings-history");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TimelineExclusionRegistrar(List<string> timeline) : IMediaMonitoringExclusionRegistrar
    {
        public string Register(string path)
        {
            timeline.Add("coordinator-exclusion");
            return Path.GetFullPath(path);
        }
    }

    private sealed class FixedVolumeEnumerator(VolumeDescriptor descriptor) : IVolumeEnumerator
    {
        public ValueTask<IReadOnlyList<VolumeDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<VolumeDescriptor>>([descriptor]);
        }
    }

    private sealed class OneChangeSource(ExternalMediaChange change) : IExternalMediaChangeSource
    {
        public async IAsyncEnumerable<ExternalMediaChange> ReadChangesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return change;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpLifecycle : IMonitoringLifecycle
    {
        public ValueTask RestartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
