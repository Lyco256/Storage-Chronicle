using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
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
    public static void Main(string[] args)
    {
        var previousArtifactsOutput = Environment.GetEnvironmentVariable("UseArtifactsOutput");
        Environment.SetEnvironmentVariable("UseArtifactsOutput", "false");
        var job = Job.Default
            .WithInvocationCount(1)
            .WithIterationCount(1)
            .WithWarmupCount(0)
            .WithUnrollFactor(1)
            .DontEnforcePowerPlan();
        var config = ManualConfig.Create(DefaultConfig.Instance).AddJob(job);
        try
        {
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
        }
        finally
        {
            Environment.SetEnvironmentVariable("UseArtifactsOutput", previousArtifactsOutput);
        }
    }
}

/// <summary>Measures production projection and state paths over deterministic event material.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
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
        var canonicalCount = await current.CountEventsAsync(canonical: true).ConfigureAwait(false);
        if (canonicalCount != BenchmarkFixtures.EventCount100K)
            throw new InvalidDataException($"The 100K storage fixture expected {BenchmarkFixtures.EventCount100K} canonical records but indexed {canonicalCount}.");

        return canonicalCount;
    }

    /// <summary>Streams one million deterministic small metadata-only records through real bounded segment and SQLite batches.</summary>
    [Benchmark]
    [BenchmarkCategory("1M", "Storage", "SQLite", "Segment")]
    public async Task<int> SegmentAppendAndSqliteIndex1M()
    {
        var current = store ?? throw new InvalidOperationException("The storage engine was not initialized.");
        const int batchSize = 512;
        for (var firstSequence = 1; firstSequence <= BenchmarkFixtures.EventCount1M; firstSequence += batchSize)
        {
            var count = Math.Min(batchSize, BenchmarkFixtures.EventCount1M - firstSequence + 1);
            var batch = BenchmarkFixtures.CreateCanonicalEventBatch(firstSequence, count, mediaTagged: false);
            await current.AppendCanonicalBatchAsync(batch).ConfigureAwait(false);
        }

        var canonicalCount = await current.CountEventsAsync(canonical: true).ConfigureAwait(false);
        if (canonicalCount != BenchmarkFixtures.EventCount1M)
            throw new InvalidDataException($"The 1M storage fixture expected {BenchmarkFixtures.EventCount1M} canonical records but indexed {canonicalCount}.");

        return canonicalCount;
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
public class MediaManifestBenchmarks : IDisposable
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
        var (volumeId, fileSystem) = BenchmarkMediaFileSystem.Open(root);
        store = new ExternalMediaStore(root, "benchmark-writer", volumeId, fileSystem, new FixedMediaClock(), writeAuthorization: _ => true);
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
        store?.Dispose();
        store = null;
        segment = null;
        BenchmarkFixtures.DeleteTemporaryDirectory(root);
        root = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Measures the real external-media segment writer on a fresh temporary media root.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class MediaSegmentAppendBenchmarks : IDisposable
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
        var (volumeId, fileSystem) = BenchmarkMediaFileSystem.Open(root);
        store = new ExternalMediaStore(root, "benchmark-writer", volumeId, fileSystem, new FixedMediaClock(), writeAuthorization: _ => true);
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
        store?.Dispose();
        store = null;
        BenchmarkFixtures.DeleteTemporaryDirectory(root);
        root = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        IterationCleanup();
        GC.SuppressFinalize(this);
    }
}

internal static partial class BenchmarkFixtures
{
    private const string OwnerMarkerName = ".storage-chronicle-benchmark-owner";
    private const int ErrorAlreadyExists = 183;

    public const int EventCount100K = 100_000;
    public const int EventCount1M = 1_000_000;
    public const int FileCount1M = EventCount1M;

    public static CanonicalEvent[] CreateCanonicalEvents(int count, bool mediaTagged, EventOrigin origin = EventOrigin.LiveUsn, EventQuality quality = EventQuality.Exact) =>
        CreateCanonicalEventBatch(1, count, mediaTagged, origin, quality);

    public static CanonicalEvent[] CreateCanonicalEventBatch(int firstSequence, int count, bool mediaTagged, EventOrigin origin = EventOrigin.LiveUsn, EventQuality quality = EventQuality.Exact)
    {
        var volume = VolumeId.Create(mediaTagged ? "volume-media-benchmark" : "volume-benchmark");
        var values = new CanonicalEvent[count];
        for (var index = 0; index < values.Length; index++)
        {
            var number = firstSequence + index;
            var fileId = FileId.Create($"file-{number:D8}");
            var properties = ImmutableDictionary<string, string>.Empty.Add("path", $"benchmark\\file-{number:D8}.dat");
            if (mediaTagged) properties = properties.Add("media.logicalMediaId", "media-benchmark");
            var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(number - 1);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Benchmark fixture roots require exclusive Windows directory creation.");

        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var runId = Guid.NewGuid();
            var path = Path.Combine(temporaryRoot, $"storage-chronicle-{name}-{runId:N}");
            if (CreateDirectoryW(path, 0) == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorAlreadyExists) continue;
                throw new Win32Exception(error, "Could not exclusively create the benchmark-owned temporary directory.");
            }

            var markerPath = Path.Combine(path, OwnerMarkerName);
            try
            {
                using var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                var markerBytes = System.Text.Encoding.ASCII.GetBytes(runId.ToString("N"));
                marker.Write(markerBytes);
                marker.Flush(flushToDisk: true);
                var dataPath = Path.Combine(path, "data");
                if (CreateDirectoryW(dataPath, 0) == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the isolated benchmark data directory.");
                return dataPath;
            }
            catch
            {
                // The directory was exclusively created by this call. Non-recursive cleanup can
                // remove it only if no unexpected child appeared before ownership was recorded.
                try { Directory.Delete(path, recursive: false); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        throw new IOException("Could not allocate a collision-free benchmark fixture directory after 64 attempts.");
    }

    public static void DeleteTemporaryDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var fullPath = Path.GetFullPath(path);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var containerPath = Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (containerPath is null) throw new IOException("Refusing to remove a benchmark path without its run-owned container directory.");
        var containerParent = Path.GetDirectoryName(containerPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (containerParent is null) throw new IOException("Refusing to remove a benchmark container outside the temporary directory root.");
        var leaf = Path.GetFileName(containerPath);
        var runIdText = leaf[(leaf.LastIndexOf('-') + 1)..];
        if (!string.Equals(containerParent, temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(fullPath), "data", StringComparison.OrdinalIgnoreCase) ||
            !leaf.StartsWith("storage-chronicle-", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(runIdText, "N", out var runId) ||
            !Directory.Exists(containerPath) ||
            !Directory.Exists(fullPath))
            throw new IOException("Refusing to remove a benchmark path outside its run-owned temporary directory boundary.");

        var containerAttributes = File.GetAttributes(containerPath);
        var dataAttributes = File.GetAttributes(fullPath);
        if ((containerAttributes & FileAttributes.ReparsePoint) != 0 || (dataAttributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing to remove a benchmark fixture directory that became a reparse point.");

        var markerPath = Path.Combine(containerPath, OwnerMarkerName);
        var marker = File.ReadAllText(markerPath, System.Text.Encoding.ASCII);
        if (!string.Equals(marker, runId.ToString("N"), StringComparison.Ordinal))
            throw new IOException("Refusing to remove a benchmark fixture without its matching run ownership marker.");

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(containerPath);
        while (pendingDirectories.Count > 0)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(pendingDirectories.Pop()))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing recursive cleanup because a benchmark fixture contains a reparse point.");
                if (Directory.Exists(child)) pendingDirectories.Push(child);
            }
        }

        Directory.Delete(containerPath, recursive: true);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateDirectoryW(string path, nint securityAttributes);
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
