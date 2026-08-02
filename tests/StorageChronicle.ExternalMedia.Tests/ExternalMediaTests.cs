using System.Collections.Immutable;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.ExternalMedia;
using Xunit;

namespace StorageChronicle.ExternalMedia.Tests;

public sealed class ExternalMediaTests
{
    [Fact]
    public async Task RoundTripVerifiesSegmentCrcAndManifestSelfHashAndSelectsNewestABSlot()
    {
        var root = TestRoot();
        try
        {
            var clock = new ManualMediaClock();
            var store = new ExternalMediaStore(root, "pc-a", clock);
            var value = TestEvent("media-1", "volume-a", "mount-a");
            var segment = await store.AppendSegmentAsync([value], TestContext.Current.CancellationToken);
            var first = await store.PublishManifestAsync("media-1", null, "mount-a", [segment], TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(1));
            var second = await store.PublishManifestAsync("media-1", first.Sha256, "mount-b", [segment], TestContext.Current.CancellationToken);

            Assert.True(ExternalMediaStore.ValidateManifest(second).IsValid);
            Assert.Equal(second.Sha256, (await store.ReadManifestSlotAsync(TestContext.Current.CancellationToken))!.Sha256);
            Assert.Equal(value.EventId, Assert.Single(await store.ReadSegmentAsync(segment, TestContext.Current.CancellationToken)).EventId);
            var segmentPath = Path.Combine(store.WriterDirectory, segment.FileName);
            var segmentBytes = await File.ReadAllBytesAsync(segmentPath, TestContext.Current.CancellationToken);
            segmentBytes[^1] ^= 0x20;
            await File.WriteAllBytesAsync(segmentPath, segmentBytes, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.ReadSegmentAsync(segment, TestContext.Current.CancellationToken));
            Assert.Equal("1", second.FormatVersion);
            Assert.Equal(EventSchemaVersion.Current, second.SchemaVersion);
            Assert.Equal("1", second.ProjectionVersion);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task PcBImportsPcAConfirmedHistoryAndFlagsConcurrentWritersWithoutDuplicatingTheSameEvent()
    {
        var root = TestRoot();
        try
        {
            var value = TestEvent("media-1", "volume-a", "mount-a");
            var pcA = new ExternalMediaStore(root, "pc-a");
            var pcASegment = await pcA.AppendSegmentAsync([value], TestContext.Current.CancellationToken);
            var pcAManifest = await pcA.PublishManifestAsync("media-1", null, "mount-a", [pcASegment], TestContext.Current.CancellationToken);
            var pcB = new ExternalMediaStore(root, "pc-b");
            var pcBSegment = await pcB.AppendSegmentAsync([value], TestContext.Current.CancellationToken);
            await pcB.PublishManifestAsync("media-1", pcAManifest.Sha256, "mount-b", [pcBSegment], TestContext.Current.CancellationToken);

            var result = await new MediaHistoryImporter().ImportAsync(pcB, MediaImportLedger.Empty, new MediaOnlyFilter(VolumeId.Create("volume-a")), TestContext.Current.CancellationToken);

            Assert.Single(result.Events);
            Assert.Equal(2, result.ImportedManifestHashes.Count);
            Assert.Equal(1, result.DuplicateSegmentCount);
            Assert.Contains(MediaImportWarning.ConcurrentWritersDetected, result.Warnings);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ImportFromAnotherPcDeduplicatesSegmentsAcrossRunsAndPersistsLedger()
    {
        var root = TestRoot();
        var ledgerPath = Path.Combine(Path.GetTempPath(), "StorageChronicle.Media.Tests", Guid.NewGuid().ToString("N"), "ledger.json");
        try
        {
            var source = new ExternalMediaStore(root, "pc-a");
            var segment = await source.AppendSegmentAsync([TestEvent("media-1", "volume-a", "mount-a")], TestContext.Current.CancellationToken);
            var manifest = await source.PublishManifestAsync("media-1", null, "mount-a", [segment], TestContext.Current.CancellationToken);
            var importer = new MediaHistoryImporter();
            var first = await importer.ImportAsync(source, MediaImportLedger.Empty, new MediaOnlyFilter(VolumeId.Create("volume-a")), TestContext.Current.CancellationToken);
            var ledger = new MediaImportLedger(first.ImportedManifestHashes.ToHashSet(StringComparer.OrdinalIgnoreCase), new[] { segment.Sha256 }.ToHashSet(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var ledgerStore = new MediaImportLedgerStore(ledgerPath);
            await ledgerStore.SaveAsync(ledger, TestContext.Current.CancellationToken);
            var second = await importer.ImportAsync(source, await ledgerStore.LoadAsync(TestContext.Current.CancellationToken), new MediaOnlyFilter(VolumeId.Create("volume-a")), TestContext.Current.CancellationToken);

            Assert.Single(first.Events);
            Assert.Contains(manifest.Sha256!, first.ImportedManifestHashes);
            Assert.Empty(second.Events);
            Assert.Equal(1, second.DuplicateSegmentCount);
        }
        finally { DeleteRoot(root); DeleteRoot(Path.GetDirectoryName(ledgerPath)!); }
    }

    [Fact]
    public async Task CorruptImportLedgerRecoversAsEmptyAndCanBeReplaced()
    {
        var root = TestRoot();
        var ledgerPath = Path.Combine(Path.GetTempPath(), "StorageChronicle.Media.Tests", Guid.NewGuid().ToString("N"), "ledger.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
            await File.WriteAllBytesAsync(ledgerPath, [0xFF, 0x00, 0x01], TestContext.Current.CancellationToken);
            var store = new MediaImportLedgerStore(ledgerPath);
            Assert.Equal(MediaImportLedger.Empty.ManifestHashes, (await store.LoadAsync(TestContext.Current.CancellationToken)).ManifestHashes);
            await store.SaveAsync(MediaImportLedger.Empty, TestContext.Current.CancellationToken);
            Assert.NotEqual(new byte[] { 0xFF, 0x00, 0x01 }, await File.ReadAllBytesAsync(ledgerPath, TestContext.Current.CancellationToken));
        }
        finally { DeleteRoot(root); DeleteRoot(Path.GetDirectoryName(ledgerPath)!); }
    }

    [Fact]
    public async Task DistinctValidChildrenWithSameParentBecomeBranch()
    {
        var root = TestRoot();
        try
        {
            var clock = new ManualMediaClock();
            var store = new ExternalMediaStore(root, "pc-a", clock);
            var parent = await store.PublishManifestAsync("media-1", null, "mount-a", [], TestContext.Current.CancellationToken);
            var branchStore = new ExternalMediaStore(root, "pc-b", clock);
            clock.Advance(TimeSpan.FromSeconds(1));
            var childA = await store.PublishManifestAsync("media-1", parent.Sha256, "mount-b", [], TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(1));
            var childB = await branchStore.PublishManifestAsync("media-1", parent.Sha256, "mount-c", [], TestContext.Current.CancellationToken);

            var branch = ExternalMediaStore.DetectBranch([parent, childA, childB]);

            Assert.StartsWith("branch-", branch.Value, StringComparison.Ordinal);
            Assert.NotEqual(childA.Sha256, childB.Sha256);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task CorruptManifestAndTraversalAreRejected()
    {
        var root = TestRoot();
        try
        {
            var store = new ExternalMediaStore(root, "pc-a");
            var segment = await store.AppendSegmentAsync([TestEvent("media-1", "volume-a", "mount-a")], TestContext.Current.CancellationToken);
            await store.PublishManifestAsync("media-1", null, "mount-a", [segment], TestContext.Current.CancellationToken);
            var manifestPath = Path.Combine(store.WriterDirectory, "manifest-A.json");
            var bytes = await File.ReadAllBytesAsync(manifestPath, TestContext.Current.CancellationToken);
            bytes[^1] = (byte)(bytes[^1] ^ 0x20);
            await File.WriteAllBytesAsync(manifestPath, bytes, TestContext.Current.CancellationToken);
            Assert.Null(await store.ReadManifestSlotAsync(TestContext.Current.CancellationToken));

            await File.WriteAllTextAsync(manifestPath, "{\"formatVersion\":\"1\",\"schemaVersion\":{\"major\":1,\"minor\":0},\"logicalMediaId\":\"m\",\"writerPcId\":\"p\",\"mountSessionId\":\"s\",\"segments\":[{\"fileName\":\"..\\\\escape.seg\",\"sha256\":\"00\",\"recordCount\":1,\"writerPcId\":\"p\"}],\"createdUtc\":\"2026-01-01T00:00:00Z\",\"sha256\":\"00\",\"projectionVersion\":\"1\"}", TestContext.Current.CancellationToken);
            Assert.Null(await store.ReadManifestSlotAsync(TestContext.Current.CancellationToken));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task InterruptedWriteFinalizesOneCompleteTempAndDiscardsIncompleteTemp()
    {
        var root = TestRoot();
        try
        {
            var store = new ExternalMediaStore(root, "pc-a");
            var segment = await store.AppendSegmentAsync([TestEvent("media-1", "volume-a", "mount-a")], TestContext.Current.CancellationToken);
            var sourceBytes = await File.ReadAllBytesAsync(Path.Combine(store.WriterDirectory, segment.FileName), TestContext.Current.CancellationToken);
            var completeTemp = Path.Combine(store.WriterDirectory, "complete.tmp");
            await File.WriteAllBytesAsync(completeTemp, sourceBytes, TestContext.Current.CancellationToken);
            var complete = await store.RecoverInterruptedWriteAsync(TestContext.Current.CancellationToken);
            Assert.True(complete!.Finalized);

            await File.WriteAllBytesAsync(Path.Combine(store.WriterDirectory, "broken.tmp"), [1, 2, 3], TestContext.Current.CancellationToken);
            var discarded = await store.RecoverInterruptedWriteAsync(TestContext.Current.CancellationToken);
            Assert.True(discarded!.Discarded);
            Assert.False(File.Exists(Path.Combine(store.WriterDirectory, "broken.tmp")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task MissingLogDirectoryCreatesNewBranchAndRecoveryMarker()
    {
        var root = TestRoot();
        try
        {
            var recovery = await MediaRecovery.RecoverDeletedLogAsync(root, "media-1", "pc-b", TestContext.Current.CancellationToken);

            Assert.True(recovery.LogWasMissing);
            Assert.StartsWith("recovered-", recovery.Branch.Value, StringComparison.Ordinal);
            Assert.True(File.Exists(recovery.MarkerPath));
            Assert.True(Directory.Exists(Path.Combine(root, ".StorageChronicle", "writers", "pc-b")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void FilesystemQualityIsHonestAndNeverCreatesUsnJournals()
    {
        var ntfs = MediaQuality.Assess(new MediaVolumeDescriptor("m", VolumeId.Create("v"), "NTFS", false, true));
        var fat = MediaQuality.Assess(new MediaVolumeDescriptor("m", VolumeId.Create("v"), "FAT32", false, false));
        var exfat = MediaQuality.PlanRecovery(new MediaVolumeDescriptor("m", VolumeId.Create("v"), "exFAT", false, false), true);
        var readOnly = MediaQuality.Assess(new MediaVolumeDescriptor("m", VolumeId.Create("v"), "NTFS", true, true));

        Assert.Equal(MediaHistoryQuality.Exact, ntfs.Quality);
        Assert.Equal(MediaRecoveryKind.UsnRecovery, MediaQuality.PlanRecovery(new MediaVolumeDescriptor("m", VolumeId.Create("v"), "NTFS", false, true), true).Kind);
        Assert.Equal(MediaHistoryQuality.DirectoryBestEffort, fat.Quality);
        Assert.Equal(MediaRecoveryKind.FullReconciliation, exfat.Kind);
        Assert.False(exfat.CreatesUsnJournal);
        Assert.Equal(MediaHistoryQuality.ReadOnly, readOnly.Quality);
    }

    [Fact]
    public void MediaOnlyFilterExcludesSystemEvents()
    {
        var media = TestEvent("media-1", "volume-media", "mount-media");
        var system = TestEvent("system", "volume-system", "mount-system");
        var logical = media with { Properties = media.Properties.SetItem("media.logicalMediaId", "logical-a") };
        var filter = new MediaOnlyFilter(VolumeId.Create("volume-media"), "logical-a", MountSessionId.Create("mount-media"));

        Assert.Single(MediaEventFilter.Apply([logical, system], filter));
        Assert.True(MediaEventFilter.IsRelated(logical, filter));
        Assert.False(MediaEventFilter.IsRelated(system, filter));
    }

    [Fact]
    public void MirrorPolicyAndDedicatedDirectoryAreProtected()
    {
        var root = TestRoot();
        try
        {
            Assert.Throws<InvalidOperationException>(() => ExternalMediaStore.ValidateMirrorConfiguration(new MediaMirrorConfiguration(true, root, IsSystemVolume: true)));
            var store = new ExternalMediaStore(root, "pc-a");
            var registry = new MediaMonitoringExclusionRegistry();
            var excluded = store.RegisterMonitoringExclusion(registry);

            Assert.Contains(excluded, registry.Paths);
            Assert.EndsWith(".StorageChronicle", excluded, StringComparison.OrdinalIgnoreCase);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void MountSessionsLinkPreviousSessionWithoutClockCorrection()
    {
        var clock = new ManualMediaClock();
        var tracker = new MountSessionTracker(clock);
        var first = tracker.Start(VolumeId.Create("v"), "pc-a", MonitoringContinuity.Continuous);
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = tracker.Start(VolumeId.Create("v"), "pc-a", MonitoringContinuity.JournalRecovered);

        Assert.Equal(first.Id, second.PreviousSession);
        Assert.Equal(first.ConnectedUtc.AddMinutes(1), second.ConnectedUtc);
        Assert.Equal(MonitoringContinuity.JournalRecovered, second.Continuity);
    }

    [Fact]
    public void UnsupportedManifestVersionIsNotAccepted()
    {
        var manifest = new MediaManifest("2", EventSchemaVersion.Current, "m", "pc", "mount", null, [], DateTimeOffset.UtcNow, "00", "1");
        Assert.False(ExternalMediaStore.ValidateManifest(manifest).IsValid);
    }

    [Fact]
    public async Task CancellationStopsAnInProgressMediaWrite()
    {
        var root = TestRoot();
        try
        {
            var store = new ExternalMediaStore(root, "pc-a");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.AppendSegmentAsync([TestEvent("media-1", "volume-a", "mount-a")], cancellation.Token));
        }
        finally { DeleteRoot(root); }
    }

    private static CanonicalEvent TestEvent(string logicalMediaId, string volume, string mount)
    {
        var now = DateTimeOffset.UtcNow;
        var properties = ImmutableDictionary<string, string>.Empty
            .Add("media.logicalMediaId", logicalMediaId)
            .Add("media.source", "external");
        return new(EventId.New(), EventSchemaVersion.Current, CanonicalOperation.Create, EventOrigin.LiveUsn, VolumeId.Create(volume), FileId.Create(Guid.NewGuid().ToString("N")), null, "file.txt", null, null, new EventTime(now, TimeSpan.Zero, null, now, new SourceSequence(1), new MountSequence(1)), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, MountSessionId.Create(mount), null, properties);
    }

    private static string TestRoot() => Path.Combine(Path.GetTempPath(), "StorageChronicle.Media.Tests", Guid.NewGuid().ToString("N"));
    private static void DeleteRoot(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); }
}
