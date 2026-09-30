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
            await WriteFixtureBytesAsync(root, segmentPath, segmentBytes, TestContext.Current.CancellationToken);
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
        var ledgerPath = Path.Combine(root, "ledger", "ledger.json");
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
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task CorruptImportLedgerFailsClosedAndSavePreservesOriginalBytes()
    {
        var root = TestRoot();
        var ledgerPath = Path.Combine(root, "ledger", "ledger.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
            await WriteFixtureBytesAsync(root, ledgerPath, [0xFF, 0x00, 0x01], TestContext.Current.CancellationToken);
            var store = new MediaImportLedgerStore(ledgerPath);
            var original = await File.ReadAllBytesAsync(ledgerPath, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.LoadAsync(TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.SaveAsync(MediaImportLedger.Empty, TestContext.Current.CancellationToken));

            Assert.Equal(original, await File.ReadAllBytesAsync(ledgerPath, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(ledgerPath)!, "*.tmp"));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ImportLedgerRejectsMissingArraysUnknownFieldsUnsupportedSchemaAndMalformedLeaves()
    {
        var invalidLedgers = new[]
        {
            "{\"manifestHashes\":[]}",
            "{\"schemaVersion\":99,\"manifestHashes\":[],\"segmentHashes\":[],\"branchHashes\":[]}",
            "{\"manifestHashes\":[],\"segmentHashes\":[],\"branchHashes\":[],\"unexpected\":true}",
            "{\"manifestHashes\":[],\"manifestHashes\":[],\"segmentHashes\":[],\"branchHashes\":[]}",
            "{\"schemaVersion\":1,\"manifestHashes\":null,\"segmentHashes\":[],\"branchHashes\":[]}",
            "not-json"
        };

        foreach (var invalid in invalidLedgers)
        {
            var root = TestRoot();
            var ledgerPath = Path.Combine(root, "ledger", "ledger.json");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
                await WriteFixtureTextAsync(root, ledgerPath, invalid, TestContext.Current.CancellationToken);
                var before = await File.ReadAllBytesAsync(ledgerPath, TestContext.Current.CancellationToken);
                var store = new MediaImportLedgerStore(ledgerPath);

                await Assert.ThrowsAsync<InvalidDataException>(async () => await store.LoadAsync(TestContext.Current.CancellationToken));
                await Assert.ThrowsAsync<InvalidDataException>(async () => await store.SaveAsync(MediaImportLedger.Empty, TestContext.Current.CancellationToken));

                Assert.Equal(before, await File.ReadAllBytesAsync(ledgerPath, TestContext.Current.CancellationToken));
            }
            finally { DeleteRoot(root); }
        }
    }

    [Fact]
    public async Task ImportLedgerRecoversOneValidInterruptedWriteWithoutOverwriting()
    {
        var root = TestRoot();
        var ledgerPath = Path.Combine(root, "ledger", "ledger.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
            var manifestHash = new string('a', 64);
            var temporary = ledgerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await WriteFixtureTextAsync(root, temporary, "{\"schemaVersion\":1,\"manifestHashes\":[\"" + manifestHash + "\"],\"segmentHashes\":[],\"branchHashes\":[\"linear\"]}", TestContext.Current.CancellationToken);

            var recovered = await new MediaImportLedgerStore(ledgerPath).LoadAsync(TestContext.Current.CancellationToken);

            Assert.Contains(manifestHash, recovered.ManifestHashes);
            Assert.Contains("linear", recovered.BranchHashes);
            Assert.True(File.Exists(ledgerPath));
            Assert.False(File.Exists(temporary));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ImportLedgerRecoveryCollisionPreservesDestinationAndTemporaryBytes()
    {
        var root = TestRoot();
        var ledgerPath = Path.Combine(root, "ledger", "ledger.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
            const string validLedger = "{\"schemaVersion\":1,\"manifestHashes\":[],\"segmentHashes\":[],\"branchHashes\":[]}";
            var temporary = ledgerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await WriteFixtureTextAsync(root, ledgerPath, validLedger, TestContext.Current.CancellationToken);
            await WriteFixtureTextAsync(root, temporary, validLedger, TestContext.Current.CancellationToken);
            var destinationBefore = await File.ReadAllBytesAsync(ledgerPath, TestContext.Current.CancellationToken);
            var temporaryBefore = await File.ReadAllBytesAsync(temporary, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(async () => await new MediaImportLedgerStore(ledgerPath).LoadAsync(TestContext.Current.CancellationToken));

            Assert.Equal(destinationBefore, await File.ReadAllBytesAsync(ledgerPath, TestContext.Current.CancellationToken));
            Assert.Equal(temporaryBefore, await File.ReadAllBytesAsync(temporary, TestContext.Current.CancellationToken));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task UnrecognizedLedgerDirectoryLeafBlocksSaveAndRemainsUntouched()
    {
        var root = TestRoot();
        var ledgerPath = Path.Combine(root, "ledger", "aabb.json");
        var unknownPath = Path.Combine(root, "ledger", "notes.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
            const string unknownContents = "{\"notALedger\":true}";
            await WriteFixtureTextAsync(root, unknownPath, unknownContents, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(async () => await new MediaImportLedgerStore(ledgerPath).SaveAsync(MediaImportLedger.Empty, TestContext.Current.CancellationToken));

            Assert.False(File.Exists(ledgerPath));
            Assert.Equal(unknownContents, await File.ReadAllTextAsync(unknownPath, TestContext.Current.CancellationToken));
        }
        finally { DeleteRoot(root); }
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
            await WriteFixtureBytesAsync(root, manifestPath, bytes, TestContext.Current.CancellationToken);
            Assert.Null(await store.ReadManifestSlotAsync(TestContext.Current.CancellationToken));

            await WriteFixtureTextAsync(root, manifestPath, "{\"formatVersion\":\"1\",\"schemaVersion\":{\"major\":1,\"minor\":0},\"logicalMediaId\":\"m\",\"writerPcId\":\"p\",\"mountSessionId\":\"s\",\"segments\":[{\"fileName\":\"..\\\\escape.seg\",\"sha256\":\"00\",\"recordCount\":1,\"writerPcId\":\"p\"}],\"createdUtc\":\"2026-01-01T00:00:00Z\",\"sha256\":\"00\",\"projectionVersion\":\"1\"}", TestContext.Current.CancellationToken);
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
            var completeTemp = Path.Combine(store.WriterDirectory, Guid.NewGuid().ToString("N") + ".tmp");
            await WriteFixtureBytesAsync(root, completeTemp, sourceBytes, TestContext.Current.CancellationToken);
            var complete = await store.RecoverInterruptedWriteAsync(TestContext.Current.CancellationToken);
            Assert.True(complete!.Finalized);

            var brokenTemp = Path.Combine(store.WriterDirectory, Guid.NewGuid().ToString("N") + ".tmp");
            await WriteFixtureBytesAsync(root, brokenTemp, [1, 2, 3], TestContext.Current.CancellationToken);
            var discarded = await store.RecoverInterruptedWriteAsync(TestContext.Current.CancellationToken);
            Assert.True(discarded!.Discarded);
            Assert.False(File.Exists(brokenTemp));

            var unrelated = Path.Combine(store.WriterDirectory, "notes.tmp");
            await WriteFixtureTextAsync(root, unrelated, "user data", TestContext.Current.CancellationToken);
            Assert.Null(await store.RecoverInterruptedWriteAsync(TestContext.Current.CancellationToken));
            Assert.Equal("user data", await File.ReadAllTextAsync(unrelated, TestContext.Current.CancellationToken));
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
    public async Task ExistingUnownedMediaDirectoryIsNeverModified()
    {
        var root = TestRoot();
        try
        {
            var logRoot = Path.Combine(root, ".StorageChronicle");
            Directory.CreateDirectory(logRoot);
            var sentinel = Path.Combine(logRoot, "important.txt");
            await WriteFixtureTextAsync(root, sentinel, "keep exactly", TestContext.Current.CancellationToken);

            Assert.Throws<IOException>(() => new ExternalMediaStore(root, "pc-a"));
            Assert.Equal("keep exactly", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(Path.Combine(logRoot, "writers")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task NonCreatingImportReadsHistoryWithoutInitializingAbsentMediaRoot()
    {
        var root = TestRoot();
        try
        {
            var source = new ExternalMediaStore(root, "pc-a", createIfMissing: false);

            var result = await new MediaHistoryImporter().ImportAsync(source, MediaImportLedger.Empty, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Empty(result.Events);
            Assert.False(Directory.Exists(Path.Combine(root, ".StorageChronicle")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void WriterRefusesToCreateMissingConfiguredMediaRoot()
    {
        var root = TestRoot();
        try
        {
            var missingMediaRoot = Path.Combine(root, "not-mounted");

            Assert.Throws<DirectoryNotFoundException>(() => new ExternalMediaStore(missingMediaRoot, "pc-a"));

            Assert.False(Directory.Exists(missingMediaRoot));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task NonCreatingImportReadsOwnedHistoryWithoutChangingMediaFiles()
    {
        var root = TestRoot();
        try
        {
            var writer = new ExternalMediaStore(root, "pc-a");
            var segment = await writer.AppendSegmentAsync([TestEvent("media-1", "volume-a", "mount-a")], TestContext.Current.CancellationToken);
            await writer.PublishManifestAsync("media-1", null, "mount-a", [segment], TestContext.Current.CancellationToken);
            var before = Directory.EnumerateFiles(writer.WriterDirectory, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(writer.MediaLogDirectory, path), path => File.ReadAllBytes(path), StringComparer.OrdinalIgnoreCase);
            var reader = new ExternalMediaStore(root, "pc-b", createIfMissing: false);

            var result = await new MediaHistoryImporter().ImportAsync(reader, MediaImportLedger.Empty, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Single(result.Events);
            var after = Directory.EnumerateFiles(writer.WriterDirectory, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(writer.MediaLogDirectory, path), path => File.ReadAllBytes(path), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            foreach (var path in before.Keys) Assert.Equal(before[path], after[path]);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task RecoveryRefusesExistingLogPathWithoutChangingItsContents()
    {
        var root = TestRoot();
        try
        {
            var logRoot = Path.Combine(root, ".StorageChronicle");
            Directory.CreateDirectory(logRoot);
            var sentinel = Path.Combine(logRoot, "keep.bin");
            await WriteFixtureBytesAsync(root, sentinel, [4, 5, 6], TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<IOException>(async () => await MediaRecovery.RecoverDeletedLogAsync(root, "media-1", "pc-a", TestContext.Current.CancellationToken));

            Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(sentinel, TestContext.Current.CancellationToken));
            Assert.Single(Directory.EnumerateFiles(logRoot));
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
    public async Task AppendBoundaryRejectsUnrelatedSystemEventAndPersistsOnlyValidMediaEvents()
    {
        var root = TestRoot();
        try
        {
            var store = new ExternalMediaStore(root, "pc-a");
            var media = TestEvent("media-1", "volume-media", "mount-media");
            var system = TestEvent("system", "volume-system", "mount-system");

            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.AppendSegmentAsync([media, system], TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateFiles(store.WriterDirectory, "*.seg"));
            Assert.Empty(Directory.EnumerateFiles(store.WriterDirectory, "*.tmp"));

            var segment = await store.AppendSegmentAsync([media], TestContext.Current.CancellationToken);

            Assert.Equal(1, segment.RecordCount);
            Assert.Equal(media.EventId, Assert.Single(await store.ReadSegmentAsync(segment, TestContext.Current.CancellationToken)).EventId);
        }
        finally { DeleteRoot(root); }
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
    public void MirrorPathMustBelongToCurrentlyConnectedVolumeMountPoint()
    {
        var root = TestRoot();
        try
        {
            ExternalMediaStore.ValidateMediaRootOnVolume(Path.Combine(root, "mirror"), [root]);

            Assert.Throws<IOException>(() => ExternalMediaStore.ValidateMediaRootOnVolume(Path.Combine(root, "mirror"), [Path.Combine(root, "other-volume")]));
            Assert.Throws<IOException>(() => ExternalMediaStore.ValidateMediaRootOnVolume(Path.Combine(root, "mirror"), Array.Empty<string>()));
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

    private static string TestRoot()
    {
        var runId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), "StorageChronicle.Media.Tests", runId);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ".test-owner"), runId);
        return path;
    }

    private static async Task WriteFixtureBytesAsync(string fixtureRoot, string targetPath, byte[] bytes, CancellationToken cancellationToken)
    {
        ValidateFixtureTarget(fixtureRoot, targetPath);
        await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken);
    }

    private static async Task WriteFixtureTextAsync(string fixtureRoot, string targetPath, string value, CancellationToken cancellationToken)
    {
        ValidateFixtureTarget(fixtureRoot, targetPath);
        await File.WriteAllTextAsync(targetPath, value, cancellationToken);
    }

    private static void ValidateFixtureTarget(string fixtureRoot, string targetPath)
    {
        ValidateFixtureOwnership(fixtureRoot);
        var boundary = Path.GetFullPath(fixtureRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(targetPath);
        if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing to mutate a test path outside its owned fixture root.");
        var parent = new DirectoryInfo(Path.GetDirectoryName(target)!);
        while (parent is not null && parent.Exists)
        {
            if ((File.GetAttributes(parent.FullName) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Refusing to mutate a fixture path through a reparse point.");
            if (string.Equals(parent.FullName, Path.GetFullPath(fixtureRoot), StringComparison.OrdinalIgnoreCase)) break;
            parent = parent.Parent;
        }
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Refusing to overwrite a fixture reparse point.");
    }

    private static void ValidateFixtureOwnership(string path)
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "StorageChronicle.Media.Tests"));
        var target = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(target);
        var runId = Path.GetFileName(target);
        if (!string.Equals(parent, allowedRoot, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(runId, "N", out _))
            throw new InvalidOperationException("Refusing to modify a media test path outside its run-specific fixture root.");
        var marker = Path.Combine(target, ".test-owner");
        if (!File.Exists(marker) || !string.Equals(File.ReadAllText(marker), runId, StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to modify a media test fixture without its matching owner marker.");
    }

    private static void DeleteRoot(string path)
    {
        if (!Directory.Exists(path)) return;
        ValidateFixtureOwnership(path);
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "StorageChronicle.Media.Tests"));
        var target = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(target);
        var runId = Path.GetFileName(target);
        if (!string.Equals(parent, allowedRoot, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(runId, "N", out _))
            throw new InvalidOperationException("Refusing to clean a media test path outside its run-specific fixture root.");
        var boundary = target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories).Prepend(target))
        {
            var resolved = Path.GetFullPath(entry);
            if (!resolved.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) && !string.Equals(resolved, target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to clean a test fixture containing an out-of-root path.");
            if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to clean a test fixture containing a reparse point.");
        }
        Directory.Delete(target, true);
    }
}
