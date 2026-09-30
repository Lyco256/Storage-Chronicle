using System.Text.Json;
using System.Runtime.CompilerServices;
using StorageChronicle.Domain.Contracts;

[assembly: InternalsVisibleTo("StorageChronicle.ExternalMedia.Tests")]

namespace StorageChronicle.ExternalMedia;

/// <summary>Imports all valid confirmed manifests and segments from a media mirror.</summary>
public sealed class MediaHistoryImporter
{
    /// <summary>Imports from every writer directory while deduplicating by manifest and segment SHA-256.</summary>
    public async ValueTask<MediaImportResult> ImportAsync(ExternalMediaStore source, MediaImportLedger ledger, MediaOnlyFilter? filter = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ledger);
        filter ??= new MediaOnlyFilter();
        var manifests = new List<(ExternalMediaStore Store, MediaManifest Manifest)>();
        foreach (var writerStore in source.OpenWriterStores())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var manifest in await writerStore.ReadManifestCandidatesAsync(cancellationToken).ConfigureAwait(false)) manifests.Add((writerStore, manifest));
        }

        var warnings = new HashSet<MediaImportWarning>();
        var writerIds = manifests.Select(value => value.Manifest.WriterPcId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (writerIds.Length > 1) warnings.Add(MediaImportWarning.ConcurrentWritersDetected);
        var branch = ExternalMediaStore.DetectBranch(manifests.Select(value => value.Manifest));
        var branchHashes = new HashSet<string>(ledger.BranchHashes, StringComparer.OrdinalIgnoreCase);
        if (branch.Value != "linear") branchHashes.Add(branch.Value);
        var knownManifestHashes = manifests.Select(value => value.Manifest.Sha256!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var manifestHashes = new HashSet<string>(ledger.ManifestHashes, StringComparer.OrdinalIgnoreCase);
        var segmentHashes = new HashSet<string>(ledger.SegmentHashes, StringComparer.OrdinalIgnoreCase);
        var importedManifests = new List<string>();
        var importedSegments = new List<string>();
        var events = new List<CanonicalEvent>();
        var duplicates = 0;

        foreach (var pair in manifests.OrderBy(value => value.Manifest.CreatedUtc).ThenBy(value => value.Manifest.Sha256, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = pair.Manifest;
            if (manifest.ParentManifestSha256 is not null && !knownManifestHashes.Contains(manifest.ParentManifestSha256)) warnings.Add(MediaImportWarning.ParentManifestUnavailable);
            if (manifestHashes.Add(manifest.Sha256!)) importedManifests.Add(manifest.Sha256!);
            foreach (var segment in manifest.Segments)
            {
                if (!segmentHashes.Add(segment.Sha256))
                {
                    duplicates++;
                    continue;
                }

                try
                {
                    var segmentEvents = await pair.Store.ReadSegmentAsync(segment, cancellationToken).ConfigureAwait(false);
                    var effectiveFilter = filter.IsSpecified ? filter : new MediaOnlyFilter(LogicalMediaId: manifest.LogicalMediaId);
                    events.AddRange(MediaEventFilter.Apply(segmentEvents, effectiveFilter));
                    importedSegments.Add(segment.Sha256);
                }
                catch (InvalidDataException) { warnings.Add(MediaImportWarning.SegmentCorrupt); }
                catch (FileNotFoundException) { warnings.Add(MediaImportWarning.SegmentCorrupt); }
            }
        }

        return new MediaImportResult(events, importedManifests, duplicates, branch, warnings.Contains(MediaImportWarning.SegmentCorrupt) ? MediaHistoryQuality.UnverifiedGap : MediaHistoryQuality.Exact, warnings.OrderBy(value => value).ToArray(), importedSegments);
    }
}

/// <summary>Persists media import deduplication hashes on the PC side.</summary>
public sealed class MediaImportLedgerStore
{
    private const int CurrentSchemaVersion = 1;
    private sealed record LedgerDocument(int SchemaVersion, string[] ManifestHashes, string[] SegmentHashes, string[] BranchHashes);
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false, WriteIndented = false };
    private readonly string path;
    private readonly string directory;

    /// <summary>Creates a ledger store at the fixed PC-local product ledger path.</summary>
    /// <remarks>Arbitrary caller paths are rejected; generations are append-only and never replace an existing ledger.</remarks>
    public MediaImportLedgerStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
        directory = Path.GetDirectoryName(this.path) ?? throw new ArgumentException("A ledger path must have a parent directory.", nameof(path));
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        ProductMediaRecoveryIntentStore.ValidateCommonDataRoot(commonData);
        var productRoot = Path.GetFullPath(Path.Combine(commonData, "Storage Chronicle", "history", "media-ledgers"));
        if (!string.Equals(directory, productRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Media import ledgers may only use the fixed CommonApplicationData Storage Chronicle product directory.", nameof(path));
        var stem = Path.GetFileNameWithoutExtension(this.path);
        if (!this.path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || stem.Length == 0 || !stem.All(Uri.IsHexDigit))
            throw new ArgumentException("The product ledger filename must be the hexadecimal encoding of one logical media identity.", nameof(path));
    }

    internal MediaImportLedgerStore(string path, bool isolatedTestRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!isolatedTestRoot) throw new ArgumentException("The test-only ledger path must explicitly use an isolated fixture root.", nameof(isolatedTestRoot));
        this.path = Path.GetFullPath(path);
        directory = Path.GetDirectoryName(this.path) ?? throw new ArgumentException("A ledger path must have a parent directory.", nameof(path));
        ValidateIsolatedTestPath(this.path);
    }

    /// <summary>Loads and unions all immutable ledger generations or returns an empty ledger.</summary>
    public async ValueTask<MediaImportLedger> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return MediaImportLedger.Empty;
        await ValidateLedgerDirectoryAsync(cancellationToken).ConfigureAwait(false);
        var result = MediaImportLedger.Empty;
        foreach (var candidate in EnumerateLedgerFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = await ReadLedgerAsync(candidate, cancellationToken).ConfigureAwait(false);
            result = Union(result, next);
        }

        return result;
    }

    /// <summary>Appends a uniquely named immutable generation without replacing any existing ledger file.</summary>
    public async ValueTask SaveAsync(MediaImportLedger ledger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ValidateNoReparsePoints(directory);
        Directory.CreateDirectory(directory);
        ValidateNoReparsePoints(directory);
        await ValidateLedgerDirectoryAsync(cancellationToken).ConfigureAwait(false);
        var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!ledger.ManifestHashes.IsSupersetOf(current.ManifestHashes) || !ledger.SegmentHashes.IsSupersetOf(current.SegmentHashes) || !ledger.BranchHashes.IsSupersetOf(current.BranchHashes))
        {
            throw new InvalidOperationException("A media import ledger save cannot discard previously recorded hashes.");
        }

        ValidateLedger(ledger);
        var stem = Path.GetFileNameWithoutExtension(path);
        var generation = Path.Combine(directory, $"{stem}.{Guid.NewGuid():N}.generation.json");
        var temporary = generation + ".tmp";
        var document = new LedgerDocument(CurrentSchemaVersion, ledger.ManifestHashes.Order(StringComparer.OrdinalIgnoreCase).ToArray(), ledger.SegmentHashes.Order(StringComparer.OrdinalIgnoreCase).ToArray(), ledger.BranchHashes.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temporary, generation, overwrite: false);
    }

    private IEnumerable<string> EnumerateLedgerFiles()
    {
        var baseName = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            EnsureNotReparsePoint(entry);
            if (Directory.Exists(entry)) throw new IOException("An unexpected directory exists in the fixed media ledger directory; no files were changed.");
            var name = Path.GetFileName(entry);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An interrupted ledger write was retained; no generation was replaced or recovered.");
            if (!string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase) && !IsGenerationFor(name, stem))
                throw new IOException("An unknown file exists in the fixed media ledger directory; no files were changed.");
            yield return entry;
        }
    }

    private async ValueTask ValidateLedgerDirectoryAsync(CancellationToken cancellationToken)
    {
        ValidateNoReparsePoints(directory);
        var baseName = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        foreach (var candidate in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNotReparsePoint(candidate);
            if (Directory.Exists(candidate)) throw new IOException("An unexpected directory exists in the fixed media ledger directory; no files were changed.");
            var name = Path.GetFileName(candidate);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An interrupted ledger write was retained; no generation was replaced or recovered.");
            if (!string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase) && !IsGenerationFor(name, stem))
                throw new IOException("An unknown file exists in the fixed media ledger directory; no files were changed.");
            _ = await ReadLedgerAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsGenerationFor(string name, string stem)
    {
        var prefix = stem + ".";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".generation.json", StringComparison.OrdinalIgnoreCase)) return false;
        var generationId = name[prefix.Length..^".generation.json".Length];
        return Guid.TryParseExact(generationId, "N", out _);
    }

    private static async ValueTask<MediaImportLedger> ReadLedgerAsync(string filePath, CancellationToken cancellationToken)
    {
        ValidateNoReparsePoints(filePath);
        JsonDocument document;
        try { document = JsonDocument.Parse(await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false)); }
        catch (JsonException exception) { throw new InvalidDataException("The media import ledger is corrupt; its original bytes were retained.", exception); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The media import ledger root must be an object.");
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!fields.TryAdd(property.Name, property.Value)) throw new InvalidDataException("The media import ledger contains duplicate fields.");
            var allowed = new HashSet<string>(["schemaVersion", "manifestHashes", "segmentHashes", "branchHashes"], StringComparer.Ordinal);
            if (fields.Keys.Any(field => !allowed.Contains(field)) || !fields.TryGetValue("manifestHashes", out var manifests) || !fields.TryGetValue("segmentHashes", out var segments) || !fields.TryGetValue("branchHashes", out var branches))
                throw new InvalidDataException("The media import ledger has an unknown schema or missing required fields.");
            if (fields.TryGetValue("schemaVersion", out var schema) && (!schema.TryGetInt32(out var version) || version != CurrentSchemaVersion))
                throw new InvalidDataException("The media import ledger schema version is unsupported.");
            var result = new MediaImportLedger(ParseArray(manifests, true), ParseArray(segments, true), ParseArray(branches, false));
            ValidateLedger(result);
            return result;
        }
    }

    private static IReadOnlySet<string> ParseArray(JsonElement element, bool sha256)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new InvalidDataException("A media import ledger field is not an array.");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in element.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("A media import ledger array contains a non-string value.");
            var item = value.GetString();
            if (string.IsNullOrWhiteSpace(item) || item.Length > 128 || item.Any(char.IsControl) || (sha256 && (item.Length != 64 || !item.All(Uri.IsHexDigit))))
                throw new InvalidDataException("A media import ledger contains an invalid identity value.");
            result.Add(item);
        }
        return result;
    }

    private static void ValidateLedger(MediaImportLedger ledger)
    {
        foreach (var hash in ledger.ManifestHashes.Concat(ledger.SegmentHashes))
            if (string.IsNullOrWhiteSpace(hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("A media import ledger contains a malformed SHA-256 hash.");
        foreach (var branch in ledger.BranchHashes)
            if (string.IsNullOrWhiteSpace(branch) || branch.Length > 128 || branch.Any(char.IsControl)) throw new InvalidDataException("A media import ledger contains a malformed branch identity.");
    }

    private static MediaImportLedger Union(MediaImportLedger left, MediaImportLedger right) => new(
        left.ManifestHashes.Concat(right.ManifestHashes).ToHashSet(StringComparer.OrdinalIgnoreCase),
        left.SegmentHashes.Concat(right.SegmentHashes).ToHashSet(StringComparer.OrdinalIgnoreCase),
        left.BranchHashes.Concat(right.BranchHashes).ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static void ValidateIsolatedTestPath(string fullPath)
    {
        var testParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "StorageChronicle.Media.Tests"));
        var prefix = Path.TrimEndingDirectorySeparator(testParent) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("The internal test ledger constructor only accepts isolated media-test fixture paths.", nameof(fullPath));
        var fixtureRoot = Directory.GetParent(Path.GetDirectoryName(fullPath)!)?.FullName;
        if (fixtureRoot is null || !Guid.TryParseExact(Path.GetFileName(fixtureRoot), "N", out _) || !File.Exists(Path.Combine(fixtureRoot, ".test-owner.json")))
            throw new ArgumentException("The internal test ledger path has no run-owned fixture marker.", nameof(fullPath));
    }

    private static void ValidateNoReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            EnsureNotReparsePoint(current);
            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            current = parent;
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Media ledger paths may not traverse reparse points: {path}");
        }
    }
}
