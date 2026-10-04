using System.Runtime.CompilerServices;
using System.Text.Json;
using StorageChronicle.Agent;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.ExternalMedia;
using StorageChronicle.Platform.Windows.FileSystem;
using StorageChronicle.Settings;
using Xunit;

namespace StorageChronicle.Agent.Tests;

public sealed class ExternalMediaCollectorTests
{
    [Fact]
    public async Task LegacyMirrorSettingWithoutExplicitApprovalDoesNotImportRecoverOrAppend()
    {
        var fixtureRoot = CreateFixtureRoot(out var runId);
        var mediaRoot = Path.Combine(fixtureRoot, "media");
        var ledgerRoot = Path.Combine(fixtureRoot, "local-ledger");
        Directory.CreateDirectory(mediaRoot);
        var settings = new FixedMachineSettingsStore(new MachineSettings
        {
            MediaMirrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["media-volume"] = mediaRoot }
        });
        var descriptor = new VolumeDescriptor(VolumeId.Create("media-volume"), "NTFS", [mediaRoot], false, true, ProtectedVolumeRoles.None, true, true, IsProtectedRoleClassificationComplete: true);
        var source = new OneMediaChangeSource(new ExternalMediaChange(ExternalMediaChangeKind.Connected, DateTimeOffset.UtcNow, "test-device"));
        try
        {
            await using var collector = new WindowsExternalMediaCollector(source, new FixedVolumeEnumerator(descriptor), settings, "pc-test", path => new MediaImportLedgerStore(Path.Combine(ledgerRoot, Path.GetFileName(path)), isolatedTestRoot: true), mirrorCoordinator: new NoOpMirrorCoordinator(), fileSystemFactory: new FixtureVolumeFileSystemFactory(mediaRoot));
            var events = new List<SourceEvent>();
            await foreach (var value in collector.CollectAsync(TestContext.Current.CancellationToken)) events.Add(value);

            Assert.Contains(events, value => value.Hint == CanonicalOperation.UnverifiedGap && value.Properties.TryGetValue("reconciliationReason", out var reason) && reason.Contains("explicitly approves", StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(Path.Combine(mediaRoot, ".StorageChronicle")));
            Assert.False(Directory.Exists(ledgerRoot));
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot, runId);
        }
    }

    [Fact]
    public async Task NotificationContinuityGapInvalidatesOldMirrorSessionAndReenumeratesReconnectedVolume()
    {
        var fixtureRoot = CreateFixtureRoot(out var runId);
        var mediaRoot = Path.Combine(fixtureRoot, "media");
        Directory.CreateDirectory(mediaRoot);
        var settings = new FixedMachineSettingsStore(new MachineSettings());
        var descriptor = new VolumeDescriptor(VolumeId.Create("media-volume"), "NTFS", [mediaRoot], false, true, ProtectedVolumeRoles.None, true, true, IsProtectedRoleClassificationComplete: true);
        var source = new OneMediaChangeSource(
            new ExternalMediaChange(ExternalMediaChangeKind.Connected, DateTimeOffset.UtcNow, "device-before-gap"),
            new ExternalMediaChange(ExternalMediaChangeKind.ContinuityGap, DateTimeOffset.UtcNow, "notification queue overflow"),
            new ExternalMediaChange(ExternalMediaChangeKind.Connected, DateTimeOffset.UtcNow, "enumeration-empty"),
            new ExternalMediaChange(ExternalMediaChangeKind.Connected, DateTimeOffset.UtcNow, "device-after-gap"));
        var coordinator = new TrackingMirrorCoordinator();
        var volumes = new SequenceVolumeEnumerator([descriptor], [], [descriptor]);
        try
        {
            await using var collector = new WindowsExternalMediaCollector(source, volumes, settings, "pc-test", mirrorCoordinator: coordinator);
            var events = new List<SourceEvent>();
            await foreach (var value in collector.CollectAsync(TestContext.Current.CancellationToken)) events.Add(value);

            Assert.Equal(2, coordinator.RegisterCount);
            Assert.Equal(1, coordinator.InvalidateCount);
            var mounts = events.Where(value => value.Hint == CanonicalOperation.MountSession).ToArray();
            Assert.Equal(2, mounts.Length);
            Assert.Equal(EventQuality.UnverifiedGap, mounts[1].Quality);
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot, runId);
        }
    }

    [Fact]
    public async Task UnknownProtectedRoleClassificationPreventsRecoveryWrites()
    {
        var fixtureRoot = CreateFixtureRoot(out var runId);
        var mediaRoot = Path.Combine(fixtureRoot, "media");
        Directory.CreateDirectory(mediaRoot);
        var settings = new FixedMachineSettingsStore(new MachineSettings
        {
            MediaMirrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["media-volume"] = mediaRoot }
        });
        var descriptor = new VolumeDescriptor(VolumeId.Create("media-volume"), "NTFS", [mediaRoot], false, true, ProtectedVolumeRoles.Unknown, true, true);
        var source = new OneMediaChangeSource(new ExternalMediaChange(ExternalMediaChangeKind.Connected, DateTimeOffset.UtcNow, "test-device"));
        try
        {
            await using var collector = new WindowsExternalMediaCollector(source, new FixedVolumeEnumerator(descriptor), settings, "pc-test", mirrorCoordinator: new NoOpMirrorCoordinator());
            var events = new List<SourceEvent>();
            await foreach (var value in collector.CollectAsync(TestContext.Current.CancellationToken)) events.Add(value);

            Assert.Contains(events, value => value.Hint == CanonicalOperation.UnverifiedGap);
            Assert.False(Directory.Exists(Path.Combine(mediaRoot, ".StorageChronicle")));
        }
        finally
        {
            DeleteFixtureRoot(fixtureRoot, runId);
        }
    }

    private static string CreateFixtureRoot(out string runId)
    {
        runId = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "StorageChronicle.Media.Tests", runId);
        Directory.CreateDirectory(root);
        using var marker = new FileStream(Path.Combine(root, ".test-owner.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(marker, new { Schema = "StorageChronicle.TestFixtureOwner.v1", RunId = runId });
        return root;
    }

    private static void DeleteFixtureRoot(string root, string runId)
    {
        if (!Directory.Exists(root)) return;
        var fullRoot = Path.GetFullPath(root);
        if (!string.Equals(Path.GetDirectoryName(fullRoot), Path.Combine(Path.GetTempPath(), "StorageChronicle.Media.Tests"), StringComparison.OrdinalIgnoreCase) || Path.GetFileName(fullRoot) != runId)
            throw new IOException("The media collector fixture escaped its dedicated temp parent.");
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(fullRoot, ".test-owner.json")));
        if (marker.RootElement.GetProperty("Schema").GetString() != "StorageChronicle.TestFixtureOwner.v1" || marker.RootElement.GetProperty("RunId").GetString() != runId)
            throw new IOException("The media collector fixture ownership marker does not match this run.");
        Directory.Delete(fullRoot, recursive: true);
    }

    private sealed class FixedMachineSettingsStore(MachineSettings settings) : ISettingsStore<MachineSettings>
    {
        public SettingsLoadResult<MachineSettings> Load() => new(settings, false, false, null);
        public void Save(MachineSettings value) => throw new NotSupportedException();
    }

    private sealed class FixedVolumeEnumerator(VolumeDescriptor descriptor) : IVolumeEnumerator
    {
        public ValueTask<IReadOnlyList<VolumeDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<VolumeDescriptor>>([descriptor]);
    }

    private sealed class SequenceVolumeEnumerator(params VolumeDescriptor[][] results) : IVolumeEnumerator
    {
        private int index;

        public ValueTask<IReadOnlyList<VolumeDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Length == 0) return ValueTask.FromResult<IReadOnlyList<VolumeDescriptor>>([]);
            var current = results[Math.Min(index, results.Length - 1)];
            index++;
            return ValueTask.FromResult<IReadOnlyList<VolumeDescriptor>>(current);
        }
    }

    private sealed class OneMediaChangeSource(params ExternalMediaChange[] changes) : IExternalMediaChangeSource
    {
        public async IAsyncEnumerable<ExternalMediaChange> ReadChangesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return change;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpMirrorCoordinator : IMediaMirrorSessionCoordinator
    {
        public ValueTask RegisterAsync(MediaVolumeDescriptor media, MountSession session, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask UnregisterAsync(VolumeId volumeId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask InvalidateAsync(VolumeId volumeId) => ValueTask.CompletedTask;
    }

    private sealed class TrackingMirrorCoordinator : IMediaMirrorSessionCoordinator
    {
        public int RegisterCount { get; private set; }
        public int InvalidateCount { get; private set; }

        public ValueTask RegisterAsync(MediaVolumeDescriptor media, MountSession session, CancellationToken cancellationToken = default)
        {
            RegisterCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask UnregisterAsync(VolumeId volumeId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask InvalidateAsync(VolumeId volumeId)
        {
            InvalidateCount++;
            return ValueTask.CompletedTask;
        }
    }
}
