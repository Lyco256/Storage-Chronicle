using System.Collections.Immutable;
using System.Text.Json;
using StorageChronicle.Contracts;
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
            var store = NewStore(root, "pc-a", clock);
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
            var pcA = NewStore(root, "pc-a");
            var pcASegment = await pcA.AppendSegmentAsync([value], TestContext.Current.CancellationToken);
            var pcAManifest = await pcA.PublishManifestAsync("media-1", null, "mount-a", [pcASegment], TestContext.Current.CancellationToken);
            var pcB = NewStore(root, "pc-b");
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
        var ledgerFixtureRoot = TestRoot();
        var ledgerPath = Path.Combine(ledgerFixtureRoot, "ledger", "ledger.json");
        try
        {
            var source = NewStore(root, "pc-a");
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
        finally { DeleteRoot(root); DeleteRoot(ledgerFixtureRoot); }
    }

    [Fact]
    public async Task CorruptImportLedgerIsPreservedAndRefusesReplacement()
    {
        var root = TestRoot();
        var ledgerFixtureRoot = TestRoot();
        var ledgerPath = Path.Combine(ledgerFixtureRoot, "ledger", "ledger.json");
        try
        {
            var store = new MediaImportLedgerStore(ledgerPath);
            await store.SaveAsync(MediaImportLedger.Empty, TestContext.Current.CancellationToken);
            byte[] corrupt = [0xFF, 0x00, 0x01];
            await File.WriteAllBytesAsync(ledgerPath, corrupt, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.LoadAsync(TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.SaveAsync(MediaImportLedger.Empty, TestContext.Current.CancellationToken));
            Assert.Equal(corrupt, await File.ReadAllBytesAsync(ledgerPath, TestContext.Current.CancellationToken));
        }
        finally { DeleteRoot(root); DeleteRoot(ledgerFixtureRoot); }
    }

    [Fact]
    public async Task ExistingUnownedLedgerDirectoryIsNotAdoptedOrChanged()
    {
        var directory = TestRoot();
        var ledgerPath = Path.Combine(directory, "ledger.json");
        byte[] original = [0x55, 0x4E, 0x4F, 0x57, 0x4E, 0x45, 0x44];
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(ledgerPath, original, TestContext.Current.CancellationToken);
            var store = new MediaImportLedgerStore(ledgerPath);

            await Assert.ThrowsAsync<IOException>(async () => await store.LoadAsync(TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<IOException>(async () => await store.SaveAsync(MediaImportLedger.Empty, TestContext.Current.CancellationToken));
            Assert.Equal(original, await File.ReadAllBytesAsync(ledgerPath, TestContext.Current.CancellationToken));
        }
        finally { DeleteRoot(directory); }
    }

    [Fact]
    public async Task DistinctValidChildrenWithSameParentBecomeBranch()
    {
        var root = TestRoot();
        try
        {
            var clock = new ManualMediaClock();
            var store = NewStore(root, "pc-a", clock);
            var parent = await store.PublishManifestAsync("media-1", null, "mount-a", [], TestContext.Current.CancellationToken);
            var branchStore = NewStore(root, "pc-b", clock);
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
            var store = NewStore(root, "pc-a");
            var segment = await store.AppendSegmentAsync([TestEvent("media-1", "volume-a", "mount-a")], TestContext.Current.CancellationToken);
            await store.PublishManifestAsync("media-1", null, "mount-a", [segment], TestContext.Current.CancellationToken);
            var manifestPath = Directory.EnumerateFiles(store.WriterDirectory, "manifest-A-*.json").Single();
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
            var store = NewStore(root, "pc-a");
            var segment = await store.AppendSegmentAsync([TestEvent("media-1", "volume-a", "mount-a")], TestContext.Current.CancellationToken);
            var sourceBytes = await File.ReadAllBytesAsync(Path.Combine(store.WriterDirectory, segment.FileName), TestContext.Current.CancellationToken);
            var completeTemp = Path.Combine(store.WriterDirectory, Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllBytesAsync(completeTemp, sourceBytes, TestContext.Current.CancellationToken);
            var complete = await store.RecoverInterruptedWriteAsync(TestContext.Current.CancellationToken);
            Assert.True(complete!.Finalized);

            var unknownTemp = Path.Combine(store.WriterDirectory, "foreign.tmp");
            byte[] unknownBytes = [1, 2, 3];
            await File.WriteAllBytesAsync(unknownTemp, unknownBytes, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.RecoverInterruptedWriteAsync(TestContext.Current.CancellationToken));
            Assert.Equal(unknownBytes, await File.ReadAllBytesAsync(unknownTemp, TestContext.Current.CancellationToken));
            File.Delete(unknownTemp);

            var brokenTemp = Path.Combine(store.WriterDirectory, Guid.NewGuid().ToString("N") + ".tmp");
            byte[] brokenBytes = [1, 2, 3];
            await File.WriteAllBytesAsync(brokenTemp, brokenBytes, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.RecoverInterruptedWriteAsync(TestContext.Current.CancellationToken));
            Assert.Equal(brokenBytes, await File.ReadAllBytesAsync(brokenTemp, TestContext.Current.CancellationToken));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void ExistingUnmarkedMediaDirectoryIsRefusedWithoutChangingContents()
    {
        var root = TestRoot();
        var productDirectory = Path.Combine(root, ".StorageChronicle");
        var foreignFile = Path.Combine(productDirectory, "notes.txt");
        byte[] original = [0x4D, 0x45, 0x44, 0x49, 0x41];
        try
        {
            Directory.CreateDirectory(productDirectory);
            File.WriteAllBytes(foreignFile, original);

            Assert.Throws<IOException>(() => NewStore(root, "pc-a"));

            Assert.Equal(original, File.ReadAllBytes(foreignFile));
            Assert.False(Directory.Exists(Path.Combine(productDirectory, "writers", "pc-b")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void ReadOnlyImportStoreRejectsUnmarkedExistingMediaDirectoryWithoutChangingContents()
    {
        var root = TestRoot();
        var productDirectory = Path.Combine(root, ".StorageChronicle");
        var foreignFile = Path.Combine(productDirectory, "notes.txt");
        byte[] original = [0x52, 0x45, 0x41, 0x44];
        try
        {
            Directory.CreateDirectory(productDirectory);
            File.WriteAllBytes(foreignFile, original);

            Assert.Throws<IOException>(() => NewStore(root, "pc-a", createIfMissing: false));

            Assert.Equal(original, File.ReadAllBytes(foreignFile));
            Assert.False(File.Exists(Path.Combine(productDirectory, ".storage-chronicle-owner.json")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void ExistingOwnedRootWithUnknownContentIsRefusedBeforeCreatingWriterState()
    {
        var root = TestRoot();
        var productDirectory = Path.Combine(root, ".StorageChronicle");
        var foreignFile = Path.Combine(productDirectory, "notes.txt");
        byte[] original = [0x4E, 0x4F, 0x54, 0x45];
        try
        {
            using (NewStore(root, "pc-a")) { }
            File.WriteAllBytes(foreignFile, original);

            Assert.Throws<IOException>(() => NewStore(root, "pc-b"));

            Assert.Equal(original, File.ReadAllBytes(foreignFile));
            Assert.False(Directory.Exists(Path.Combine(productDirectory, "writers", "pc-b")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void ConfiguredMirrorRootMustBeTheConnectedVolumeMountPoint()
    {
        var root = TestRoot();

        Assert.Equal(Path.GetFullPath(root), ExternalMediaStore.ValidateMediaRoot(root, [root]));
        Assert.Throws<InvalidOperationException>(() => ExternalMediaStore.ValidateMediaRoot(Path.Combine(root, "subfolder"), [root]));
        Assert.Throws<InvalidOperationException>(() => ExternalMediaStore.ValidateMediaRoot("\\\\server\\share", [root]));
        Assert.Throws<InvalidOperationException>(() => ExternalMediaStore.ValidateMediaRoot(root, []));
    }

    [Fact]
    public async Task MissingLogDirectoryCreatesNewBranchAndRecoveryMarker()
    {
        var root = TestRoot();
        try
        {
            var recovery = await Recover(root, "media-1", "pc-b", TestContext.Current.CancellationToken);

            Assert.True(recovery.LogWasMissing);
            Assert.StartsWith("recovered-", recovery.Branch.Value, StringComparison.Ordinal);
            Assert.True(File.Exists(recovery.MarkerPath));
            Assert.True(Directory.Exists(Path.Combine(root, ".StorageChronicle", "writers", "pc-b")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task RecoveryDoesNotOverwriteAnExistingMarker()
    {
        var root = TestRoot();
        var store = NewStore(root, "pc-b");
        var markerPath = Path.Combine(store.MediaLogDirectory, "recovery-marker.json");
        byte[] original = [0x72, 0x65, 0x63, 0x6F, 0x76, 0x65, 0x72, 0x79];
        try
        {
            await File.WriteAllBytesAsync(markerPath, original, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<IOException>(async () => await Recover(root, "media-1", "pc-b", TestContext.Current.CancellationToken));

            Assert.Equal(original, await File.ReadAllBytesAsync(markerPath, TestContext.Current.CancellationToken));
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
            Assert.Throws<InvalidOperationException>(() => ExternalMediaStore.ValidateMirrorConfiguration(new MediaMirrorConfiguration(true, root)));
            Assert.Throws<InvalidOperationException>(() => { _ = ExternalMediaStore.ValidateMirrorConfiguration(new MediaMirrorConfiguration(true, root, ProtectedVolumeRoles.None, IsProtectedRoleClassificationComplete: false)); });
            Assert.True(ExternalMediaStore.ValidateMirrorConfiguration(new MediaMirrorConfiguration(true, root, ProtectedVolumeRoles.None, IsProtectedRoleClassificationComplete: true)).IsAllowed);
            foreach (var protectedRole in new[] { ProtectedVolumeRoles.System, ProtectedVolumeRoles.Boot, ProtectedVolumeRoles.Recovery, ProtectedVolumeRoles.Efi, ProtectedVolumeRoles.Unknown })
            {
                Assert.Throws<InvalidOperationException>(() => { _ = ExternalMediaStore.ValidateMirrorConfiguration(new MediaMirrorConfiguration(true, root, protectedRole, IsProtectedRoleClassificationComplete: true)); });
            }
            var store = NewStore(root, "pc-a");
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
            var store = NewStore(root, "pc-a");
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

    private static ExternalMediaStore NewStore(string root, string writer, IMediaClock? clock = null, bool createIfMissing = true)
    {
        var fullRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(fullRoot);
        var volume = VolumeId.Create(fullRoot);
        return new ExternalMediaStore(fullRoot, writer, volume, new FixtureMediaFileSystem(fullRoot, volume), clock, createIfMissing);
    }

    private static async ValueTask<MediaLogDeletionRecovery> Recover(string root, string mediaId, string writer, CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(fullRoot);
        var volume = VolumeId.Create(fullRoot);
        return await MediaRecovery.RecoverDeletedLogAsync(fullRoot, volume, new FixtureMediaFileSystem(fullRoot, volume), mediaId, writer, cancellationToken);
    }

    private static string TestRoot()
    {
        var runId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), "StorageChronicle.Media.Tests", runId);
        Directory.CreateDirectory(path);
        using var marker = new FileStream(Path.Combine(path, ".test-owner.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(marker, new { Schema = "StorageChronicle.TestFixtureOwner.v1", RunId = runId });
        return path;
    }
    private static void DeleteRoot(string path)
    {
        if (!Directory.Exists(path)) return;
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), Path.Combine(Path.GetTempPath(), "StorageChronicle.Media.Tests"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The external-media test fixture escaped its dedicated temp parent.");
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(fullPath, ".test-owner.json")));
        var runId = marker.RootElement.GetProperty("RunId").GetString();
        if (marker.RootElement.GetProperty("Schema").GetString() != "StorageChronicle.TestFixtureOwner.v1" || runId != Path.GetFileName(fullPath) || !Guid.TryParseExact(runId, "N", out _))
            throw new IOException("The external-media fixture ownership marker does not match this run.");
        EnsureNoReparsePoints(fullPath);
        Directory.Delete(fullPath, recursive: true);
    }

    private static void EnsureNoReparsePoints(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException($"An external-media fixture path is a reparse point: {directory}");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException($"A reparse point was found in the external-media fixture: {entry}");
            if (Directory.Exists(entry)) EnsureNoReparsePoints(entry);
        }
    }
}
