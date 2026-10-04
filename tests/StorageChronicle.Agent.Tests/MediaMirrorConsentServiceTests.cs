using System.Text.Json;
using StorageChronicle.Agent;
using StorageChronicle.Contracts;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.ExternalMedia;
using StorageChronicle.Settings;
using StorageChronicle.Storage;
using Xunit;

namespace StorageChronicle.Agent.Tests;

public sealed class MediaMirrorConsentServiceTests
{
    private const string ApproverSid = "S-1-5-21-100-200-300-1001";
    private static readonly string AclFingerprint = new('A', 64);

    [Fact]
    public async Task ExplicitApprovalIsRevalidatedAndPersistedBeforeAuthorizationReturns()
    {
        using var fixture = new ConsentFixture(createProductRoot: true);
        var authorization = fixture.Service.AuthorizeAsync(fixture.Media, fixture.MediaRoot).AsTask();
        var request = await fixture.WaitForRequestAsync();

        Assert.True(request.ExistingHistoryReadAndImportRequested);
        Assert.True(request.FutureHistoryAppendRequested);
        Assert.Equal(MediaMirrorAclDisclosure.NtfsAclUnavailable, request.AclDisclosure);
        Assert.True(await fixture.Service.DecideAsync(new MediaMirrorApprovalDecision(request.RequestId, Approve: true), ApproverSid));

        Assert.True(await authorization);
        using (var fileSystem = fixture.FileSystems.Open(fixture.VolumeId))
            Assert.True(fixture.Service.HasGrant(fixture.Media, fixture.MediaRoot, fileSystem));
        Assert.True(Assert.Single(fixture.MachineStore.Load().Settings.MediaMirrorConsents).ExistingHistoryReadAndImportAllowed);
        Assert.Equal(ApproverSid, Assert.Single(fixture.MachineStore.Load().Settings.MediaMirrorConsents).ApprovedUserSid);
        Assert.Equal(AclFingerprint, Assert.Single(fixture.MachineStore.Load().Settings.MediaMirrorConsents).AclDescriptorFingerprint);
    }

    [Fact]
    public async Task NewRootApprovalCreatesOnlyTheDedicatedRootAndDoesNotAuthorizeHistoryImport()
    {
        using var fixture = new ConsentFixture(createProductRoot: false);
        var authorization = fixture.Service.AuthorizeAsync(fixture.Media, fixture.MediaRoot).AsTask();
        var request = await fixture.WaitForRequestAsync();

        Assert.False(request.ExistingHistoryReadAndImportRequested);
        Assert.Equal("pending-new-root", request.DedicatedMediaRootIdentity);
        Assert.True(await fixture.Service.DecideAsync(new MediaMirrorApprovalDecision(request.RequestId, Approve: true), ApproverSid));

        Assert.True(await authorization);
        Assert.True(Directory.Exists(Path.Combine(fixture.MediaRoot, ".StorageChronicle")));
        Assert.False(fixture.Service.CanReadExistingHistory(fixture.Media, fixture.MediaRoot));
        Assert.False(Assert.Single(fixture.MachineStore.Load().Settings.MediaMirrorConsents).ExistingHistoryReadAndImportAllowed);
    }

    [Fact]
    public async Task NewRootApprovalDoesNotPersistOrAuthorizeWhenPostCreationAclScanFails()
    {
        using var fixture = new ConsentFixture(createProductRoot: false);
        fixture.CurrentAclStatus = MediaMirrorAclInspectionStatus.Unsafe;
        var authorization = fixture.Service.AuthorizeAsync(fixture.Media, fixture.MediaRoot).AsTask();
        var request = await fixture.WaitForRequestAsync();

        Assert.False(await fixture.Service.DecideAsync(new MediaMirrorApprovalDecision(request.RequestId, Approve: true), ApproverSid));

        Assert.False(await authorization);
        Assert.True(Directory.Exists(Path.Combine(fixture.MediaRoot, ".StorageChronicle")));
        Assert.Empty(fixture.MachineStore.Load().Settings.MediaMirrorConsents);
        Assert.False(fixture.Service.CanReadExistingHistory(fixture.Media, fixture.MediaRoot));
    }

    [Fact]
    public async Task ApprovalWithoutAuthenticatedUserSidCannotCreateOrGrantMirrorRoot()
    {
        using var fixture = new ConsentFixture(createProductRoot: false);
        var authorization = fixture.Service.AuthorizeAsync(fixture.Media, fixture.MediaRoot).AsTask();
        var request = await fixture.WaitForRequestAsync();

        Assert.False(await fixture.Service.DecideAsync(new MediaMirrorApprovalDecision(request.RequestId, Approve: true)));
        Assert.False(await authorization);
        Assert.Empty(fixture.MachineStore.Load().Settings.MediaMirrorConsents);
        Assert.False(Directory.Exists(Path.Combine(fixture.MediaRoot, ".StorageChronicle")));
    }

    [Fact]
    public async Task ChangedAclFingerprintInvalidatesExistingConsent()
    {
        using var fixture = new ConsentFixture(createProductRoot: true);
        var authorization = fixture.Service.AuthorizeAsync(fixture.Media, fixture.MediaRoot).AsTask();
        var request = await fixture.WaitForRequestAsync();
        Assert.True(await fixture.Service.DecideAsync(new MediaMirrorApprovalDecision(request.RequestId, Approve: true), ApproverSid));
        Assert.True(await authorization);

        fixture.CurrentAclFingerprint = new('C', 64);
        using var fileSystem = fixture.FileSystems.Open(fixture.VolumeId);
        Assert.False(fixture.Service.HasGrant(fixture.Media, fixture.MediaRoot, fileSystem));
    }

    [Fact]
    public async Task DeclineLeavesSettingsAndMediaRootUnchanged()
    {
        using var fixture = new ConsentFixture(createProductRoot: false);
        var authorization = fixture.Service.AuthorizeAsync(fixture.Media, fixture.MediaRoot).AsTask();
        var request = await fixture.WaitForRequestAsync();

        Assert.True(await fixture.Service.DecideAsync(new MediaMirrorApprovalDecision(request.RequestId, Approve: false)));

        Assert.False(await authorization);
        Assert.Empty(fixture.MachineStore.Load().Settings.MediaMirrorConsents);
        Assert.False(Directory.Exists(Path.Combine(fixture.MediaRoot, ".StorageChronicle")));
    }

    [Fact]
    public async Task ChangedRootIdentityAtDecisionTimeFailsClosedAndResolvesPendingRequest()
    {
        using var fixture = new ConsentFixture(createProductRoot: true);
        var authorization = fixture.Service.AuthorizeAsync(fixture.Media, fixture.MediaRoot).AsTask();
        var request = await fixture.WaitForRequestAsync();
        fixture.RootIdentity = "fixture-file-id-replaced";

        Assert.False(await fixture.Service.DecideAsync(new MediaMirrorApprovalDecision(request.RequestId, Approve: true), ApproverSid));
        Assert.False(await authorization);
        Assert.Empty(fixture.MachineStore.Load().Settings.MediaMirrorConsents);
    }

    [Fact]
    public async Task SettingsHistoryFailureDoesNotAuthorizeOrRetainTheConsentGrant()
    {
        using var fixture = new ConsentFixture(createProductRoot: true, failSettingsHistory: true);
        var authorization = fixture.Service.AuthorizeAsync(fixture.Media, fixture.MediaRoot).AsTask();
        var request = await fixture.WaitForRequestAsync();

        Assert.False(await fixture.Service.DecideAsync(new MediaMirrorApprovalDecision(request.RequestId, Approve: true), ApproverSid));
        Assert.False(await authorization);
        Assert.Empty(fixture.MachineStore.Load().Settings.MediaMirrorConsents);
    }

    [Fact]
    public async Task CancellingPendingApprovalRemovesRequestWithoutCreatingOrGrantingMediaRoot()
    {
        using var fixture = new ConsentFixture(createProductRoot: false);
        using var cancellation = new CancellationTokenSource();
        var authorization = fixture.Service.AuthorizeAsync(fixture.Media, fixture.MediaRoot, cancellation.Token).AsTask();
        await fixture.WaitForRequestAsync();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await authorization);
        Assert.Empty(fixture.MachineStore.Load().Settings.MediaMirrorConsents);
        Assert.False(Directory.Exists(Path.Combine(fixture.MediaRoot, ".StorageChronicle")));
        Assert.Empty(fixture.Health.Snapshot(new RecordingStatus(RecordingState.Running, 0, 0, null)).PendingMediaMirrorApprovals!);
    }

    private sealed class ConsentFixture : IDisposable
    {
        private readonly string root;
        private readonly string runId;
        private readonly AgentSettingsService settingsService;
        private readonly AgentHealthState health = new();
        private readonly FixtureVolumeFileSystemFactory fileSystems;

        public ConsentFixture(bool createProductRoot, bool failSettingsHistory = false)
        {
            runId = Guid.NewGuid().ToString("N");
            root = Path.Combine(Path.GetTempPath(), "StorageChronicle.Media.Tests", runId);
            Directory.CreateDirectory(root);
            using (var marker = new FileStream(Path.Combine(root, ".test-owner.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(marker, new { Schema = "StorageChronicle.TestFixtureOwner.v1", RunId = runId });
            MediaRoot = Path.Combine(root, "media");
            Directory.CreateDirectory(MediaRoot);
            VolumeId = VolumeId.Create("consent-fixture-volume");
            Media = new MediaVolumeDescriptor(VolumeId.Value, VolumeId, "NTFS", false, false, ProtectedVolumeRoles.None, true)
            {
                MountPoints = [MediaRoot]
            };
            MachineStore = new MutableSettingsStore<MachineSettings>(new MachineSettings
            {
                LogStoragePath = Path.Combine(root, "local-history"),
                MediaMirrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Media.LogicalMediaId] = MediaRoot }
            });
            fileSystems = new FixtureVolumeFileSystemFactory(MediaRoot, () => RootIdentity, sid =>
                sid != ApproverSid
                    ? new(MediaMirrorAclInspectionStatus.Unknown, null, 0, 0, ["WrongApproverSid"])
                    : !Directory.Exists(Path.Combine(MediaRoot, ".StorageChronicle"))
                        ? new(MediaMirrorAclInspectionStatus.Unknown, null, 0, 0, ["ProductRootNotCreated"])
                        : new(CurrentAclStatus, CurrentAclStatus == MediaMirrorAclInspectionStatus.Verified ? CurrentAclFingerprint : null, 1, 0,
                            CurrentAclStatus == MediaMirrorAclInspectionStatus.Verified ? [] : ["FixtureAclUnsafe"]));
            if (createProductRoot)
            {
                using var store = new ExternalMediaStore(MediaRoot, "pc-test", VolumeId, fileSystems.Open(VolumeId), writeAuthorization: _ => true);
            }
            settingsService = new AgentSettingsService(MachineStore, new MutableSettingsStore<UserSettings>(new UserSettings()),
                new NoOpHistory(failSettingsHistory), new AllowAllAgentSettingsAuthorizer(), new NoOpMonitoringLifecycle());
            Service = new MediaMirrorConsentService(health, MachineStore, settingsService,
                new FixedVolumeEnumerator(new VolumeDescriptor(VolumeId, "NTFS", [MediaRoot], false, true, ProtectedVolumeRoles.None, true, true, true)),
                fileSystems, "pc-test");
        }

        public string MediaRoot { get; }
        public VolumeId VolumeId { get; }
        public string RootIdentity { get; set; } = "fixture-file-id-owned-root";
        public string CurrentAclFingerprint { get; set; } = AclFingerprint;
        public MediaMirrorAclInspectionStatus CurrentAclStatus { get; set; } = MediaMirrorAclInspectionStatus.Verified;
        public MediaVolumeDescriptor Media { get; }
        public MutableSettingsStore<MachineSettings> MachineStore { get; }
        public MediaMirrorConsentService Service { get; }
        public FixtureVolumeFileSystemFactory FileSystems => fileSystems;
        public AgentHealthState Health => health;

        public async Task<PendingMediaMirrorApproval> WaitForRequestAsync()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var snapshot = health.Snapshot(new RecordingStatus(RecordingState.Running, 0, 0, null));
                if (snapshot.PendingMediaMirrorApprovals is { Count: > 0 } values) return values[0];
                await Task.Delay(10);
            }
            throw new TimeoutException("The media approval request was not queued.");
        }

        public void Dispose()
        {
            settingsService.Dispose();
            if (!Directory.Exists(root)) return;
            using (var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".test-owner.json"))))
            {
                if (marker.RootElement.GetProperty("Schema").GetString() != "StorageChronicle.TestFixtureOwner.v1" || marker.RootElement.GetProperty("RunId").GetString() != runId)
                    throw new IOException("Refusing to remove a consent fixture without its matching run marker.");
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MutableSettingsStore<T>(T value) : ISettingsStore<T> where T : class
    {
        private T current = value;
        public SettingsLoadResult<T> Load() => new(current, false, false, null);
        public void Save(T settings) => current = settings;
    }

    private sealed class FixedVolumeEnumerator(VolumeDescriptor volume) : IVolumeEnumerator
    {
        public ValueTask<IReadOnlyList<VolumeDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<VolumeDescriptor>>([volume]);
        }
    }

    private sealed class NoOpHistory(bool fail) : ISettingsChangeHistory
    {
        public ValueTask RecordAsync(SettingsChangeHistoryEvent value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fail) throw new IOException("Fixture settings history failure.");
            return ValueTask.CompletedTask;
        }
    }
}
