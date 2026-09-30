using System.Collections.Immutable;
using System.Security.Cryptography;
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
            var ledgerStore = new MediaImportLedgerStore(ledgerPath, isolatedTestRoot: true);
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
            var store = new MediaImportLedgerStore(ledgerPath, isolatedTestRoot: true);
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
    public async Task ArbitraryLedgerPathAndForgedOwnerMarkerCannotAuthorizeWrites()
    {
        var directory = TestRoot();
        var ledgerPath = Path.Combine(directory, "ledger.json");
        byte[] original = [0x55, 0x4E, 0x4F, 0x57, 0x4E, 0x45, 0x44];
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(ledgerPath, original, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, ".storage-chronicle-ledgers-owner.json"), "{\"schema\":\"StorageChronicle.MediaLedgerOwnership.v1\"}", TestContext.Current.CancellationToken);

            Assert.Throws<ArgumentException>(() => new MediaImportLedgerStore(ledgerPath));

            Assert.Equal(original, await File.ReadAllBytesAsync(ledgerPath, TestContext.Current.CancellationToken));
            var names = Directory.EnumerateFiles(directory).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(3, names.Count);
            Assert.Contains("ledger.json", names);
            Assert.Contains(".storage-chronicle-ledgers-owner.json", names);
            Assert.Contains(".test-owner.json", names);
        }
        finally { DeleteRoot(directory); }
    }

    [Fact]
    public async Task LedgerRejectsOwnerMarkerAndUnrecognizedJsonWithoutChangingEither()
    {
        var fixtureRoot = TestRoot();
        var ledgerDirectory = Path.Combine(fixtureRoot, "ledger");
        var ledgerPath = Path.Combine(ledgerDirectory, "aabb.json");
        byte[] marker = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"manifestHashes\":[],\"segmentHashes\":[],\"branchHashes\":[]}");
        byte[] unrelated = [0x7B, 0x22, 0x6E, 0x6F, 0x74, 0x65, 0x22, 0x3A, 0x31, 0x7D];
        try
        {
            Directory.CreateDirectory(ledgerDirectory);
            var markerPath = Path.Combine(ledgerDirectory, ".storage-chronicle-ledgers-owner.json");
            await File.WriteAllBytesAsync(markerPath, marker, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(ledgerDirectory, "notes.json"), unrelated, TestContext.Current.CancellationToken);
            var store = new MediaImportLedgerStore(ledgerPath, isolatedTestRoot: true);

            await Assert.ThrowsAsync<IOException>(async () => await store.LoadAsync(TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<IOException>(async () => await store.SaveAsync(MediaImportLedger.Empty, TestContext.Current.CancellationToken));

            Assert.Equal(marker, await File.ReadAllBytesAsync(markerPath, TestContext.Current.CancellationToken));
            Assert.Equal(unrelated, await File.ReadAllBytesAsync(Path.Combine(ledgerDirectory, "notes.json"), TestContext.Current.CancellationToken));
            Assert.False(File.Exists(ledgerPath));
            Assert.Empty(Directory.EnumerateFiles(ledgerDirectory, "*.generation.json"));
            Assert.Empty(Directory.EnumerateFiles(ledgerDirectory, "*.tmp"));
        }
        finally { DeleteRoot(fixtureRoot); }
    }

    [Fact]
    public async Task LedgerSaveAppendsUniqueGenerationsAndNeverReplacesExistingNames()
    {
        var fixtureRoot = TestRoot();
        var ledgerPath = Path.Combine(fixtureRoot, "ledger", "aabb.json");
        try
        {
            var store = new MediaImportLedgerStore(ledgerPath, isolatedTestRoot: true);
            var one = new MediaImportLedger(new[] { new string('a', 64) }.ToHashSet(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var two = one with { SegmentHashes = new[] { new string('b', 64) }.ToHashSet(StringComparer.OrdinalIgnoreCase) };

            await store.SaveAsync(one, TestContext.Current.CancellationToken);
            var firstName = Directory.EnumerateFiles(Path.GetDirectoryName(ledgerPath)!, "*.generation.json").Single();
            byte[] firstBytes = await File.ReadAllBytesAsync(firstName, TestContext.Current.CancellationToken);
            await store.SaveAsync(two, TestContext.Current.CancellationToken);

            var generations = Directory.EnumerateFiles(Path.GetDirectoryName(ledgerPath)!, "*.generation.json").ToArray();
            Assert.Equal(2, generations.Length);
            Assert.Contains(firstName, generations);
            Assert.Equal(firstBytes, await File.ReadAllBytesAsync(firstName, TestContext.Current.CancellationToken));
            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Contains(new string('a', 64), loaded.ManifestHashes);
            Assert.Contains(new string('b', 64), loaded.SegmentHashes);
        }
        finally { DeleteRoot(fixtureRoot); }
    }

    [Fact]
    public async Task LedgerRejectsMalformedSchemaAndPreservesEveryExistingByte()
    {
        var invalidDocuments = new[]
        {
            "{\"manifestHashes\":[]}",
            "{\"schemaVersion\":99,\"manifestHashes\":[],\"segmentHashes\":[],\"branchHashes\":[]}",
            "{\"manifestHashes\":[],\"segmentHashes\":[],\"branchHashes\":[],\"unexpected\":true}",
            "{\"manifestHashes\":[],\"manifestHashes\":[],\"segmentHashes\":[],\"branchHashes\":[]}",
            "{\"schemaVersion\":1,\"manifestHashes\":null,\"segmentHashes\":[],\"branchHashes\":[]}",
            "{\"schemaVersion\":1,\"manifestHashes\":[\"bad-hash\"],\"segmentHashes\":[],\"branchHashes\":[]}",
            "not-json"
        };

        foreach (var invalid in invalidDocuments)
        {
            var fixtureRoot = TestRoot();
            var ledgerPath = Path.Combine(fixtureRoot, "ledger", "aabb.json");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
                await File.WriteAllTextAsync(ledgerPath, invalid, TestContext.Current.CancellationToken);
                var original = await File.ReadAllBytesAsync(ledgerPath, TestContext.Current.CancellationToken);
                var store = new MediaImportLedgerStore(ledgerPath, isolatedTestRoot: true);

                await Assert.ThrowsAsync<InvalidDataException>(async () => await store.LoadAsync(TestContext.Current.CancellationToken));
                await Assert.ThrowsAsync<InvalidDataException>(async () => await store.SaveAsync(MediaImportLedger.Empty, TestContext.Current.CancellationToken));

                Assert.Equal(original, await File.ReadAllBytesAsync(ledgerPath, TestContext.Current.CancellationToken));
                Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(ledgerPath)!));
            }
            finally { DeleteRoot(fixtureRoot); }
        }
    }

    [Fact]
    public async Task LedgerTemporaryWithoutIntentIsPreservedAndBlocksNewGeneration()
    {
        var fixtureRoot = TestRoot();
        var ledgerPath = Path.Combine(fixtureRoot, "ledger", "aabb.json");
        try
        {
            var directory = Path.GetDirectoryName(ledgerPath)!;
            Directory.CreateDirectory(directory);
            var tempPath = ledgerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] original = [0x7B, 0x22, 0x75, 0x6E, 0x6B, 0x6E, 0x6F, 0x77, 0x6E, 0x22, 0x3A, 0x74, 0x72, 0x75, 0x65, 0x7D];
            await File.WriteAllBytesAsync(tempPath, original, TestContext.Current.CancellationToken);
            var store = new MediaImportLedgerStore(ledgerPath, isolatedTestRoot: true);

            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.LoadAsync(TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.SaveAsync(MediaImportLedger.Empty, TestContext.Current.CancellationToken));

            Assert.Equal(original, await File.ReadAllBytesAsync(tempPath, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(ledgerPath));
            Assert.Single(Directory.EnumerateFileSystemEntries(directory));
        }
        finally { DeleteRoot(fixtureRoot); }
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
    public async Task InterruptedWriteFinalizesIntentBackedTempAndPreservesCorruptTemp()
    {
        var root = TestRoot();
        var intents = new FixtureRecoveryIntentStore();
        try
        {
            var store = NewStore(root, "pc-a", recoveryIntents: intents);
            var segment = await store.AppendSegmentAsync([TestEvent("media-1", "volume-a", "mount-a")], TestContext.Current.CancellationToken);
            var sourceBytes = await File.ReadAllBytesAsync(Path.Combine(store.WriterDirectory, segment.FileName), TestContext.Current.CancellationToken);
            var completeName = Guid.NewGuid().ToString("N") + ".tmp";
            var completeTemp = Path.Combine(store.WriterDirectory, completeName);
            await File.WriteAllBytesAsync(completeTemp, sourceBytes, TestContext.Current.CancellationToken);
            intents.Add(new MediaRecoveryIntent(VolumeId.Create(Path.GetFullPath(root)), "pc-a", completeName, sourceBytes.Length, Convert.ToHexString(SHA256.HashData(sourceBytes))));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.RecoverInterruptedWriteAsync(cancelled.Token));
            }
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(completeTemp, TestContext.Current.CancellationToken));
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
            var brokenName = Path.GetFileName(brokenTemp);
            intents.Add(new MediaRecoveryIntent(VolumeId.Create(Path.GetFullPath(root)), "pc-a", brokenName, brokenBytes.Length, Convert.ToHexString(SHA256.HashData(brokenBytes))));
            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.RecoverInterruptedWriteAsync(TestContext.Current.CancellationToken));
            Assert.Equal(brokenBytes, await File.ReadAllBytesAsync(brokenTemp, TestContext.Current.CancellationToken));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ValidExistingTempWithoutPcLocalIntentIsPreservedEvenWithOwnedMediaMarker()
    {
        var root = TestRoot();
        try
        {
            var store = NewStore(root, "pc-a", recoveryIntents: new FixtureRecoveryIntentStore());
            var segment = await store.AppendSegmentAsync([TestEvent("media-1", "volume-a", "mount-a")], TestContext.Current.CancellationToken);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(store.WriterDirectory, segment.FileName), TestContext.Current.CancellationToken);
            var tempName = Guid.NewGuid().ToString("N") + ".tmp";
            var tempPath = Path.Combine(store.WriterDirectory, tempName);
            await File.WriteAllBytesAsync(tempPath, bytes, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.RecoverInterruptedWriteAsync(TestContext.Current.CancellationToken));

            Assert.Equal(bytes, await File.ReadAllBytesAsync(tempPath, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.ChangeExtension(tempPath, ".seg")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task MalformedPcLocalIntentCannotAuthorizeTempRecovery()
    {
        var root = TestRoot();
        var intents = new FixtureRecoveryIntentStore();
        try
        {
            var store = NewStore(root, "pc-a", recoveryIntents: intents);
            var segment = await store.AppendSegmentAsync([TestEvent("media-1", "volume-a", "mount-a")], TestContext.Current.CancellationToken);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(store.WriterDirectory, segment.FileName), TestContext.Current.CancellationToken);
            var name = Guid.NewGuid().ToString("N") + ".tmp";
            var tempPath = Path.Combine(store.WriterDirectory, name);
            await File.WriteAllBytesAsync(tempPath, bytes, TestContext.Current.CancellationToken);
            var volume = VolumeId.Create(Path.GetFullPath(root));
            intents.AddFor(volume, "pc-a", name, new MediaRecoveryIntent(volume, "pc-a", name, bytes.Length, null!));

            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.RecoverInterruptedWriteAsync(TestContext.Current.CancellationToken));

            Assert.Equal(bytes, await File.ReadAllBytesAsync(tempPath, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.ChangeExtension(tempPath, ".seg")));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task RecoveryDestinationCollisionPreservesBothExistingSegmentAndTemp()
    {
        var root = TestRoot();
        var intents = new FixtureRecoveryIntentStore();
        try
        {
            var store = NewStore(root, "pc-a", recoveryIntents: intents);
            var segment = await store.AppendSegmentAsync([TestEvent("media-1", "volume-a", "mount-a")], TestContext.Current.CancellationToken);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(store.WriterDirectory, segment.FileName), TestContext.Current.CancellationToken);
            var id = Guid.NewGuid().ToString("N");
            var tempName = id + ".tmp";
            var tempPath = Path.Combine(store.WriterDirectory, tempName);
            var finalPath = Path.Combine(store.WriterDirectory, id + ".seg");
            byte[] collision = [0x43, 0x4F, 0x4C, 0x4C, 0x49, 0x44, 0x45];
            await File.WriteAllBytesAsync(tempPath, bytes, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(finalPath, collision, TestContext.Current.CancellationToken);
            intents.Add(new MediaRecoveryIntent(VolumeId.Create(Path.GetFullPath(root)), "pc-a", tempName, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));

            await Assert.ThrowsAsync<IOException>(async () => await store.RecoverInterruptedWriteAsync(TestContext.Current.CancellationToken));

            Assert.Equal(bytes, await File.ReadAllBytesAsync(tempPath, TestContext.Current.CancellationToken));
            Assert.Equal(collision, await File.ReadAllBytesAsync(finalPath, TestContext.Current.CancellationToken));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task AppendSegmentRejectsMixedMediaBatchBeforeCreatingSegmentFiles()
    {
        var root = TestRoot();
        try
        {
            var store = NewStore(root, "pc-a");
            var mediaEvent = TestEvent("media-1", "volume-a", "mount-a");
            var foreignEvent = TestEvent("other-media", "volume-b", "mount-b");

            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.AppendSegmentAsync([mediaEvent, foreignEvent], TestContext.Current.CancellationToken));

            Assert.Empty(Directory.EnumerateFiles(store.WriterDirectory, "*.tmp"));
            Assert.Empty(Directory.EnumerateFiles(store.WriterDirectory, "*.seg"));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task IntentSaveFailureOrCancellationLeavesOnlyTheNewTemporaryAndNeverFinalizesIt()
    {
        var root = TestRoot();
        try
        {
            var intents = new FixtureRecoveryIntentStore { FailSave = true };
            var store = NewStore(root, "pc-a", recoveryIntents: intents);
            await Assert.ThrowsAsync<IOException>(async () => await store.AppendSegmentAsync([TestEvent("media-1", "volume-a", "mount-a")], TestContext.Current.CancellationToken));
            var temp = Assert.Single(Directory.EnumerateFiles(store.WriterDirectory, "*.tmp"));
            var original = await File.ReadAllBytesAsync(temp, TestContext.Current.CancellationToken);
            Assert.NotEmpty(original);
            Assert.Empty(Directory.EnumerateFiles(store.WriterDirectory, "*.seg"));
            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.RecoverInterruptedWriteAsync(TestContext.Current.CancellationToken));
            Assert.Equal(original, await File.ReadAllBytesAsync(temp, TestContext.Current.CancellationToken));

            intents.FailSave = false;
            intents.CancelSave = true;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.AppendSegmentAsync([TestEvent("media-1", "volume-a", "mount-a")], TestContext.Current.CancellationToken));
            Assert.Equal(2, Directory.EnumerateFiles(store.WriterDirectory, "*.tmp").Count());
            Assert.Empty(Directory.EnumerateFiles(store.WriterDirectory, "*.seg"));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task IntentRemovalFailureDoesNotUndoOrReplaceFinalizedSegment()
    {
        var root = TestRoot();
        try
        {
            var intents = new FixtureRecoveryIntentStore { FailRemove = true };
            var store = NewStore(root, "pc-a", recoveryIntents: intents);

            var segment = await store.AppendSegmentAsync([TestEvent("media-1", "volume-a", "mount-a")], TestContext.Current.CancellationToken);

            Assert.True(File.Exists(Path.Combine(store.WriterDirectory, segment.FileName)));
            Assert.Empty(Directory.EnumerateFiles(store.WriterDirectory, "*.tmp"));
            Assert.NotEmpty(intents.Pending);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task IntentCleanupUnauthorizedFailureDoesNotTurnCommittedAppendIntoFailure()
    {
        var root = TestRoot();
        try
        {
            var intents = new FixtureRecoveryIntentStore { FailUnauthorizedRemove = true };
            var store = NewStore(root, "pc-a", recoveryIntents: intents);

            var segment = await store.AppendSegmentAsync([TestEvent("media-1", "volume-a", "mount-a")], TestContext.Current.CancellationToken);

            Assert.True(File.Exists(Path.Combine(store.WriterDirectory, segment.FileName)));
            Assert.Equal(segment.ByteLength, new FileInfo(Path.Combine(store.WriterDirectory, segment.FileName)).Length);
            Assert.NotEmpty(segment.Sha256);
            Assert.NotEmpty(intents.Pending);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task CancellationAfterDurableMoveDoesNotFailAppendOrRequirePostCommitRead()
    {
        var root = TestRoot();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var intents = new FixtureRecoveryIntentStore { BeforeRemove = cancellation.Cancel };
            var store = NewStore(root, "pc-a", recoveryIntents: intents);

            var segment = await store.AppendSegmentAsync([TestEvent("media-1", "volume-a", "mount-a")], cancellation.Token);

            Assert.True(cancellation.IsCancellationRequested);
            var finalPath = Path.Combine(store.WriterDirectory, segment.FileName);
            Assert.True(File.Exists(finalPath));
            Assert.Equal(segment.ByteLength, new FileInfo(finalPath).Length);
            Assert.Empty(Directory.EnumerateFiles(store.WriterDirectory, "*.tmp"));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ProductIntentStoreCleansOnlyItsCancelledTempAndRejectsPathBypass()
    {
        var root = TestRoot();
        try
        {
            var store = new ProductMediaRecoveryIntentStore(root, isolatedTestRoot: true);
            var intent = new MediaRecoveryIntent(VolumeId.Create("fixture-volume"), "pc-a", Guid.NewGuid().ToString("N") + ".tmp", 3, new string('a', 64));
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.SaveAsync(intent, cancellation.Token));
            }

            var intentDirectory = Path.Combine(root, "Storage Chronicle", "history", "media-recovery-intents");
            Assert.Empty(Directory.EnumerateFileSystemEntries(intentDirectory));

            await store.SaveAsync(intent, TestContext.Current.CancellationToken);
            Assert.Equal(intent, await store.FindAsync(intent.VolumeId, intent.WriterPcId, intent.TemporaryFileName, TestContext.Current.CancellationToken));
            await store.RemoveAsync(intent.VolumeId, intent.WriterPcId, intent.TemporaryFileName, TestContext.Current.CancellationToken);
            Assert.Empty(Directory.EnumerateFileSystemEntries(intentDirectory));

            var unknownPath = Path.Combine(intentDirectory, "unowned.bin");
            byte[] unknown = [0x55, 0x4E, 0x4F, 0x57, 0x4E];
            await File.WriteAllBytesAsync(unknownPath, unknown, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<IOException>(async () => await store.SaveAsync(intent with { TemporaryFileName = Guid.NewGuid().ToString("N") + ".tmp" }, TestContext.Current.CancellationToken));
            Assert.Equal(unknown, await File.ReadAllBytesAsync(unknownPath, TestContext.Current.CancellationToken));
            Assert.Single(Directory.EnumerateFileSystemEntries(intentDirectory));

            Assert.Throws<IOException>(() => ProductMediaRecoveryIntentStore.ValidateCommonDataRoot("relative-data-root"));
            Assert.Throws<IOException>(() => ProductMediaRecoveryIntentStore.ValidateCommonDataRoot("\\\\server\\share"));
            Assert.Throws<ArgumentException>(() => new ProductMediaRecoveryIntentStore(root, isolatedTestRoot: false));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ProductIntentStorePreflightsJunctionBeforeCreatingEscapedDirectoriesWhenSupported()
    {
        var root = TestRoot();
        var target = TestRoot();
        var linkPath = Path.Combine(root, "Storage Chronicle");
        var linkCreated = false;
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, target);
                linkCreated = true;
            }
            catch (UnauthorizedAccessException) { return; }
            catch (PlatformNotSupportedException) { return; }
            catch (IOException) { return; }

            var store = new ProductMediaRecoveryIntentStore(root, isolatedTestRoot: true);
            var intent = new MediaRecoveryIntent(VolumeId.Create("fixture-volume"), "pc-a", Guid.NewGuid().ToString("N") + ".tmp", 3, new string('a', 64));

            await Assert.ThrowsAsync<IOException>(async () => await store.SaveAsync(intent, TestContext.Current.CancellationToken));

            Assert.False(Directory.Exists(Path.Combine(target, "history")));
            Assert.Single(Directory.EnumerateFileSystemEntries(target));
            Assert.True(File.Exists(Path.Combine(target, ".test-owner.json")));
        }
        finally
        {
            if (linkCreated) Directory.Delete(linkPath);
            DeleteRoot(target);
            DeleteRoot(root);
        }
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

    private static ExternalMediaStore NewStore(string root, string writer, IMediaClock? clock = null, bool createIfMissing = true, FixtureRecoveryIntentStore? recoveryIntents = null)
    {
        var fullRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(fullRoot);
        var volume = VolumeId.Create(fullRoot);
        return new ExternalMediaStore(fullRoot, writer, volume, new FixtureMediaFileSystem(fullRoot, volume), clock, createIfMissing, recoveryIntents ?? new FixtureRecoveryIntentStore());
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

    private sealed class FixtureRecoveryIntentStore : IMediaRecoveryIntentStore
    {
        private readonly Dictionary<(VolumeId Volume, string Writer, string Name), MediaRecoveryIntent> intents = new();

        public bool FailSave { get; set; }
        public bool CancelSave { get; set; }
        public bool FailRemove { get; set; }
        public bool FailUnauthorizedRemove { get; set; }
        public Action? BeforeRemove { get; set; }
        public IReadOnlyCollection<MediaRecoveryIntent> Pending => intents.Values;

        public void Add(MediaRecoveryIntent intent) => intents.Add((intent.VolumeId, intent.WriterPcId, intent.TemporaryFileName), intent);
        public void AddFor(VolumeId volume, string writer, string name, MediaRecoveryIntent intent) => intents.Add((volume, writer, name), intent);

        public ValueTask SaveAsync(MediaRecoveryIntent intent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailSave) throw new IOException("Fixture intent save failure.");
            if (CancelSave) throw new OperationCanceledException("Fixture intent save cancellation.", cancellationToken);
            var key = (intent.VolumeId, intent.WriterPcId, intent.TemporaryFileName);
            if (intents.TryGetValue(key, out var existing) && existing != intent) throw new IOException("Conflicting fixture intent.");
            intents[key] = intent;
            return ValueTask.CompletedTask;
        }

        public ValueTask<MediaRecoveryIntent?> FindAsync(VolumeId volumeId, string writerPcId, string temporaryFileName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            intents.TryGetValue((volumeId, writerPcId, temporaryFileName), out var intent);
            return ValueTask.FromResult(intent);
        }

        public ValueTask RemoveAsync(VolumeId volumeId, string writerPcId, string temporaryFileName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailRemove) throw new IOException("Fixture intent removal failure.");
            if (FailUnauthorizedRemove) throw new UnauthorizedAccessException("Fixture intent removal permission failure.");
            BeforeRemove?.Invoke();
            intents.Remove((volumeId, writerPcId, temporaryFileName));
            return ValueTask.CompletedTask;
        }
    }
}
