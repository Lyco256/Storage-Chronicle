using System.Collections.Immutable;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.ExternalMedia;
using StorageChronicle.Projection;
using StorageChronicle.State;
using StorageChronicle.Storage;

namespace StorageChronicle.Benchmarks;

/// <summary>Entry point for opt-in performance measurements; all durable writes use temporary roots.</summary>
public static class Program
{
    /// <summary>Runs the selected BenchmarkDotNet suites.</summary>
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

/// <summary>Measures production projection and state paths over deterministic event material.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
[InvocationCount(1)]
[IterationCount(1)]
[WarmupCount(0)]
public class StorageChronicleBenchmarks
{
    private CanonicalEvent[] events = [];
    private ProjectionDocument document = null!;
    private StateEngine state = null!;
    private DateTimeOffset stateTime;

    /// <summary>Builds deterministic event material and a real one-million-object state engine.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        events = BenchmarkFixtures.CreateCanonicalEvents(BenchmarkFixtures.EventCount100K, mediaTagged: false);
        document = new ProjectionDocument(events);
        state = new StateEngine();
        var stateEvents = BenchmarkFixtures.CreateCanonicalEvents(BenchmarkFixtures.FileCount1M, mediaTagged: false, EventOrigin.MftReconciliation, EventQuality.Reconciled);
        foreach (var value in stateEvents) await state.ApplyAsync(value).ConfigureAwait(false);
        stateTime = stateEvents[^1].Time.RecordedUtc;
    }

    /// <summary>Reconstructs a point-in-time state view from the real one-million-object state engine.</summary>
    [Benchmark]
    [BenchmarkCategory("1M", "State")]
    public async Task<string> ReconstructSinglePointPath1M()
    {
        var snapshot = await state.GetSnapshotAsync(stateTime).ConfigureAwait(false);
        var entry = snapshot.Entries.Count > 0 ? snapshot.Entries[^1] : snapshot.UnplacedEntries[^1];
        return entry.ReconstructedPath ?? throw new InvalidOperationException("The real state snapshot did not reconstruct the benchmark path.");
    }

    /// <summary>Runs the production grouped Event Stack projector over one hundred thousand events.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "Projection")]
    public int GroupedGeneration100K()
    {
        var projector = new EventStackProjector();
        var page = projector.GetPage(document, new EventStackQuery(EventStackMode.Grouped, 1, 250), new ProjectionSettings());
        return page.TotalCount;
    }

    /// <summary>Materializes one bounded Event Stack page through the production projector.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "EventStack")]
    public int EventStackPage100K()
    {
        var projector = new EventStackProjector();
        var page = projector.GetPage(document, new EventStackQuery(EventStackMode.Normalized, 200, 250), new ProjectionSettings());
        return page.Items.Count;
    }

    /// <summary>Computes a period diff through the production path and semantic grouping implementation.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "Diff")]
    public int PeriodDiff100K()
    {
        var projector = new DiffProjector();
        var end = events[^1].Time.RecordedUtc;
        var result = projector.Project(document, events[0].Time.RecordedUtc, end, DiffMode.Period, ProjectionFilter.Empty);
        return result.Items.Count + result.UnknownLocationItems.Count;
    }
}

/// <summary>Measures recording one large folder move without creating descendant move events.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
[InvocationCount(1)]
[IterationCount(1)]
[WarmupCount(0)]
public class LargeFolderMoveBenchmarks
{
    private const int ChildCount = 100_000;
    private static readonly VolumeId Volume = VolumeId.Create("volume-folder-move-benchmark");
    private static readonly FileId OldParent = FileId.Create("folder-move-old-parent");
    private static readonly FileId NewParent = FileId.Create("folder-move-new-parent");
    private StateEngine state = null!;
    private CanonicalEvent folderMove = null!;

    /// <summary>Builds a real state with one hundred thousand children and one pending parent move.</summary>
    [IterationSetup]
    public void Setup()
    {
        state = new StateEngine();
        state.ApplyAsync(BenchmarkEvent(1, CanonicalOperation.DirectoryCreate, OldParent, null, "old", FileKind.Directory)).AsTask().GetAwaiter().GetResult();
        state.ApplyAsync(BenchmarkEvent(2, CanonicalOperation.DirectoryCreate, NewParent, null, "new", FileKind.Directory)).AsTask().GetAwaiter().GetResult();
        for (var index = 0; index < ChildCount; index++)
        {
            var sequence = 3L + index;
            state.ApplyAsync(BenchmarkEvent(sequence, CanonicalOperation.Create, FileId.Create($"folder-child-{index:D6}"), OldParent, $"child-{index:D6}.dat", FileKind.File)).AsTask().GetAwaiter().GetResult();
        }

        folderMove = BenchmarkEvent(ChildCount + 3L, CanonicalOperation.Move, OldParent, NewParent, "old", FileKind.Directory);
    }

    /// <summary>Applies one parent move and verifies that the operation count grows by exactly one.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "State", "LargeFolderMove")]
    public async Task<int> RecordLargeFolderMove()
    {
        var before = state.GetAppliedEvents().Length;
        await state.ApplyAsync(folderMove).ConfigureAwait(false);
        var after = state.GetAppliedEvents().Length;
        if (after != before + 1) throw new InvalidOperationException("A folder move must be recorded as one parent event without synthetic descendant events.");
        return state.GetDirectoryEntryHistory(OldParent)?.Versions.Length ?? 0;
    }

    private static CanonicalEvent BenchmarkEvent(long sequence, CanonicalOperation operation, FileId fileId, FileId? parent, string name, FileKind kind)
    {
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(sequence);
        var properties = ImmutableDictionary<string, string>.Empty.Add("parentKnown", "true");
        var metadata = new FileMetadata(Volume, fileId, parent, name, kind, null, null, time, time, time, time, 0, null, null, EventQuality.Exact, true, false);
        return new CanonicalEvent(EventId.New(), EventSchemaVersion.Current, operation, EventOrigin.InitialSnapshot, Volume, fileId, parent, name, null, metadata,
            new EventTime(time, TimeSpan.Zero, time, time, new SourceSequence(sequence), new MountSequence(sequence)), EventQuality.Exact, null,
            ProcessAttributionQuality.Unknown, null, null, properties);
    }
}

/// <summary>Measures real append-only segment writes and their coupled SQLite index writes.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
[InvocationCount(1)]
[IterationCount(1)]
[WarmupCount(0)]
public class StorageAppendBenchmarks : IAsyncDisposable
{
    private CanonicalEvent[] events = [];
    private AppendOnlyStorageEngine? store;
    private string? root;

    /// <summary>Creates the deterministic append payload outside the measured operation.</summary>
    [GlobalSetup]
    public void Setup() => events = BenchmarkFixtures.CreateCanonicalEvents(BenchmarkFixtures.EventCount100K, mediaTagged: false);

    /// <summary>Creates a fresh real storage directory for every measured iteration.</summary>
    [IterationSetup]
    public void IterationSetup()
    {
        root = BenchmarkFixtures.CreateTemporaryDirectory("storage-append");
        store = new AppendOnlyStorageEngine(BenchmarkFixtures.CreateStorageOptions(root));
    }

    /// <summary>Appends one hundred thousand canonical events to the real segment log and SQLite index.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "Storage", "SQLite", "Segment")]
    public async Task<int> SegmentAppendAndSqliteIndex100K()
    {
        var current = store ?? throw new InvalidOperationException("The storage engine was not initialized.");
        await BenchmarkBatchHelpers.AppendInBoundedBatchesAsync(current, events).ConfigureAwait(false);
        return await current.CountEventsAsync(canonical: true).ConfigureAwait(false);
    }

    /// <summary>Closes a real populated store so final segment flush and Zstandard close work are measured.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "Storage", "Compression")]
    public async Task<long> FlushAndCloseCompressedSegment100K()
    {
        var current = store ?? throw new InvalidOperationException("The storage engine was not initialized.");
        await BenchmarkBatchHelpers.AppendInBoundedBatchesAsync(current, events).ConfigureAwait(false);
        await current.StopAsync().ConfigureAwait(false);
        var segments = await current.EnumerateHealthySegmentsAsync().ConfigureAwait(false);
        return segments.Sum(value => value.RecordCount);
    }

    /// <summary>Disposes the real engine and removes only the benchmark-owned temporary directory.</summary>
    [IterationCleanup]
    public void IterationCleanup()
    {
        if (store is not null) store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        store = null;
        BenchmarkFixtures.DeleteTemporaryDirectory(root);
        root = null;
    }

    /// <summary>Provides a final no-op-safe cleanup for benchmark runners that honor async disposal.</summary>
    public async ValueTask DisposeAsync()
    {
        if (store is not null) await store.DisposeAsync().ConfigureAwait(false);
        store = null;
        BenchmarkFixtures.DeleteTemporaryDirectory(root);
        root = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>Measures rebuilding and querying the real on-disk SQLite index from immutable segments.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
[InvocationCount(1)]
[IterationCount(1)]
[WarmupCount(0)]
public class SqliteRebuildBenchmarks : IAsyncDisposable
{
    private CanonicalEvent[] events = [];
    private AppendOnlyStorageEngine? store;
    private string? root;

    /// <summary>Creates one real segment set and its SQLite index outside the measured rebuild.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        root = BenchmarkFixtures.CreateTemporaryDirectory("sqlite-rebuild");
        events = BenchmarkFixtures.CreateCanonicalEvents(BenchmarkFixtures.EventCount100K, mediaTagged: false);
        store = new AppendOnlyStorageEngine(BenchmarkFixtures.CreateStorageOptions(root));
        await BenchmarkBatchHelpers.AppendInBoundedBatchesAsync(store, events).ConfigureAwait(false);
        await store.StopAsync().ConfigureAwait(false);
        if (!File.Exists(Path.Combine(root, "index.sqlite"))) throw new InvalidOperationException("The storage setup did not create a real SQLite index.");
    }

    /// <summary>Recreates the SQLite database by reading the real immutable segment records.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "SQLite", "Recovery")]
    public async Task<int> SqliteIndexRebuild100K()
    {
        var current = store ?? throw new InvalidOperationException("The storage engine was not initialized.");
        await current.RebuildSqliteAsync().ConfigureAwait(false);
        return await current.CountEventsAsync(canonical: true).ConfigureAwait(false);
    }

    /// <summary>Queries the real SQLite event index without materializing segment payloads.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "SQLite", "Query")]
    public async Task<int> SqliteIndexedCount100K()
    {
        var current = store ?? throw new InvalidOperationException("The storage engine was not initialized.");
        return await current.CountEventsAsync(canonical: true).ConfigureAwait(false);
    }

    /// <summary>Releases SQLite handles and removes only the benchmark-owned temporary directory.</summary>
    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (store is not null) await store.DisposeAsync().ConfigureAwait(false);
        store = null;
        BenchmarkFixtures.DeleteTemporaryDirectory(root);
        root = null;
    }

    /// <summary>Provides a final no-op-safe cleanup for benchmark runners that honor async disposal.</summary>
    public async ValueTask DisposeAsync()
    {
        if (store is not null) await store.DisposeAsync().ConfigureAwait(false);
        store = null;
        BenchmarkFixtures.DeleteTemporaryDirectory(root);
        root = null;
        GC.SuppressFinalize(this);
    }

}

/// <summary>Measures real external-media segment writes, manifest publication, and manifest import.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
[InvocationCount(1)]
[IterationCount(1)]
[WarmupCount(0)]
public class MediaManifestBenchmarks
{
    private CanonicalEvent[] events = [];
    private ExternalMediaStore? store;
    private MediaSegment? segment;
    private string? root;

    /// <summary>Writes one real media segment and a self-hashed A/B manifest outside the import measurement.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        root = BenchmarkFixtures.CreateTemporaryDirectory("media-manifest");
        events = BenchmarkFixtures.CreateCanonicalEvents(BenchmarkFixtures.EventCount100K, mediaTagged: true);
        store = new ExternalMediaStore(root, "benchmark-writer", new FixedMediaClock());
        segment = await store.AppendSegmentAsync(events).ConfigureAwait(false);
        await store.PublishManifestAsync("media-benchmark", null, "mount-benchmark", new[] { segment }).ConfigureAwait(false);
        var manifest = await store.ReadManifestSlotAsync().ConfigureAwait(false);
        if (manifest is null || !ExternalMediaStore.ValidateManifest(manifest).IsValid) throw new InvalidOperationException("The setup did not create a valid self-hashed media manifest.");
    }

    /// <summary>Reads and validates the real A/B manifest and every referenced segment.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "Media", "Manifest", "Segment")]
    public async Task<int> MediaManifestImport100K()
    {
        var current = store ?? throw new InvalidOperationException("The media store was not initialized.");
        var result = await new MediaHistoryImporter().ImportAsync(current, MediaImportLedger.Empty).ConfigureAwait(false);
        return result.Events.Count;
    }

    /// <summary>Confirms that the measured manifest references the expected real segment.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "Media", "Manifest")]
    public async Task<int> MediaManifestReadAndValidate100K()
    {
        var current = store ?? throw new InvalidOperationException("The media store was not initialized.");
        var manifest = await current.ReadManifestSlotAsync().ConfigureAwait(false) ?? throw new InvalidDataException("The media manifest is missing.");
        var validation = ExternalMediaStore.ValidateManifest(manifest);
        if (!validation.IsValid || manifest.Segments.Count != 1 || manifest.Segments[0].RecordCount != events.Length) throw new InvalidDataException(validation.Error ?? "The media manifest does not describe the benchmark segment.");
        return manifest.Segments[0].RecordCount;
    }

    /// <summary>Releases media handles and removes only the benchmark-owned temporary directory.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        store = null;
        segment = null;
        BenchmarkFixtures.DeleteTemporaryDirectory(root);
        root = null;
    }
}

/// <summary>Measures the real external-media segment writer on a fresh temporary media root.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
[InvocationCount(1)]
[IterationCount(1)]
[WarmupCount(0)]
public class MediaSegmentAppendBenchmarks
{
    private CanonicalEvent[] events = [];
    private ExternalMediaStore? store;
    private string? root;

    /// <summary>Creates deterministic canonical media events outside the measured append.</summary>
    [GlobalSetup]
    public void Setup() => events = BenchmarkFixtures.CreateCanonicalEvents(BenchmarkFixtures.EventCount100K, mediaTagged: true);

    /// <summary>Creates a new real media writer directory for every measured iteration.</summary>
    [IterationSetup]
    public void IterationSetup()
    {
        root = BenchmarkFixtures.CreateTemporaryDirectory("media-segment");
        store = new ExternalMediaStore(root, "benchmark-writer", new FixedMediaClock());
    }

    /// <summary>Appends and hashes one hundred thousand canonical events in a real media segment.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "Media", "Segment")]
    public async Task<long> MediaSegmentAppend100K()
    {
        var current = store ?? throw new InvalidOperationException("The media store was not initialized.");
        var segment = await current.AppendSegmentAsync(events).ConfigureAwait(false);
        return segment.ByteLength;
    }

    /// <summary>Removes the real temporary media root after all file handles are closed.</summary>
    [IterationCleanup]
    public void IterationCleanup()
    {
        store = null;
        BenchmarkFixtures.DeleteTemporaryDirectory(root);
        root = null;
    }
}

internal static class BenchmarkFixtures
{
    public const int EventCount100K = 100_000;
    public const int FileCount1M = 1_000_000;

    public static CanonicalEvent[] CreateCanonicalEvents(int count, bool mediaTagged, EventOrigin origin = EventOrigin.LiveUsn, EventQuality quality = EventQuality.Exact)
    {
        var volume = VolumeId.Create(mediaTagged ? "volume-media-benchmark" : "volume-benchmark");
        var values = new CanonicalEvent[count];
        for (var index = 0; index < values.Length; index++)
        {
            var number = index + 1;
            var fileId = FileId.Create($"file-{number:D8}");
            var properties = ImmutableDictionary<string, string>.Empty.Add("path", $"benchmark\\file-{number:D8}.dat");
            if (mediaTagged) properties = properties.Add("media.logicalMediaId", "media-benchmark");
            var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(index);
            values[index] = new CanonicalEvent(
                new EventId(new Guid($"00000000-0000-0000-0000-{number:D12}")),
                EventSchemaVersion.Current,
                CanonicalOperation.DataWrite,
                origin,
                volume,
                fileId,
                null,
                $"file-{number:D8}.dat",
                null,
                null,
                new EventTime(timestamp, TimeSpan.Zero, timestamp, timestamp, new SourceSequence(number), new MountSequence(number)),
                quality,
                null,
                ProcessAttributionQuality.Unknown,
                null,
                null,
                properties);
        }

        return values;
    }

    public static StorageEngineOptions CreateStorageOptions(string directory) => new(directory)
    {
        SegmentMaxBytes = 64 * 1024 * 1024,
        FlushInterval = TimeSpan.FromHours(1),
        BusyTimeout = TimeSpan.FromSeconds(30),
        CompressClosedSegments = true,
        HistoryBranch = "benchmark"
    };

    public static string CreateTemporaryDirectory(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"storage-chronicle-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteTemporaryDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        Directory.Delete(path, recursive: true);
    }
}

internal static class BenchmarkBatchHelpers
{
    public static async Task AppendInBoundedBatchesAsync(AppendOnlyStorageEngine store, IReadOnlyList<CanonicalEvent> values)
    {
        foreach (var batch in values.Chunk(512)) await store.AppendCanonicalBatchAsync(batch).ConfigureAwait(false);
    }
}

internal sealed class FixedMediaClock : IMediaClock
{
    public DateTimeOffset UtcNow => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
