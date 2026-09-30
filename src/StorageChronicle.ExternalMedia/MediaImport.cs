using System.Text.Json;
using StorageChronicle.Domain.Contracts;

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
        var writersRoot = Path.Combine(source.MediaLogDirectory, "writers");
        if (Directory.Exists(writersRoot))
        {
            foreach (var writerDirectory in Directory.EnumerateDirectories(writersRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var writer = Path.GetFileName(writerDirectory);
                if (string.IsNullOrWhiteSpace(writer)) continue;
                var writerStore = new ExternalMediaStore(source.MediaRoot, writer, createIfMissing: false);
                foreach (var manifest in await writerStore.ReadManifestCandidatesAsync(cancellationToken).ConfigureAwait(false)) manifests.Add((writerStore, manifest));
            }
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
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = false };
    private readonly string path;

    /// <summary>Creates a ledger store at a PC-side path.</summary>
    public MediaImportLedgerStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    /// <summary>Loads a recognized ledger, recovers one unambiguous interrupted write, or returns empty when absent.</summary>
    public async ValueTask<MediaImportLedger> LoadAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory)) await ValidateLedgerLeavesAsync(directory, cancellationToken).ConfigureAwait(false);
        var temporaryFiles = FindTemporaryFiles(directory);
        if (File.Exists(path))
        {
            if (temporaryFiles.Length != 0) throw new InvalidDataException("The import ledger and an interrupted replacement both exist; neither was changed.");
            return await ReadLedgerAsync(path, cancellationToken).ConfigureAwait(false);
        }

        if (temporaryFiles.Length == 0) return MediaImportLedger.Empty;
        if (temporaryFiles.Length != 1) throw new InvalidDataException("Multiple interrupted import-ledger writes were found; recovery is ambiguous.");
        var recovered = await ReadLedgerAsync(temporaryFiles[0], cancellationToken).ConfigureAwait(false);
        try
        {
            File.Move(temporaryFiles[0], path, false);
            return recovered;
        }
        catch (IOException exception)
        {
            throw new IOException("The interrupted import ledger could not be recovered without overwriting another file.", exception);
        }
    }

    /// <summary>Saves the ledger atomically so a process stop cannot erase prior deduplication.</summary>
    public async ValueTask SaveAsync(MediaImportLedger ledger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("The import ledger must have a parent directory.");
        Directory.CreateDirectory(directory);
        await ValidateLedgerLeavesAsync(directory, cancellationToken).ConfigureAwait(false);
        var temporaryFiles = FindTemporaryFiles(directory);
        if (temporaryFiles.Length != 0) throw new InvalidDataException("An interrupted import-ledger write must be recovered before saving; no file was changed.");
        if (File.Exists(path)) _ = await ReadLedgerAsync(path, cancellationToken).ConfigureAwait(false);
        ValidateLedger(ledger);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var document = new LedgerDocument(CurrentSchemaVersion, ledger.ManifestHashes.Order(StringComparer.OrdinalIgnoreCase).ToArray(), ledger.SegmentHashes.Order(StringComparer.OrdinalIgnoreCase).ToArray(), ledger.BranchHashes.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, true);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private string[] FindTemporaryFiles(string directory)
    {
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        var prefix = Path.GetFileName(path) + ".";
        return Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".*.tmp", SearchOption.TopDirectoryOnly)
            .Where(candidate => Path.GetFileName(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static async ValueTask ValidateLedgerLeavesAsync(string directory, CancellationToken cancellationToken)
    {
        foreach (var candidate in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await ReadLedgerAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<MediaImportLedger> ReadLedgerAsync(string filePath, CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("A reparse-point import ledger is not accepted.");
        JsonDocument document;
        try { document = JsonDocument.Parse(await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false)); }
        catch (JsonException exception) { throw new InvalidDataException("The import ledger contains malformed JSON.", exception); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The import ledger root must be an object.");
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!fields.TryAdd(property.Name, property.Value)) throw new InvalidDataException("The import ledger contains duplicate fields.");
            }

            var allowed = new HashSet<string>(["schemaVersion", "manifestHashes", "segmentHashes", "branchHashes"], StringComparer.Ordinal);
            if (fields.Keys.Any(field => !allowed.Contains(field)) ||
                !fields.TryGetValue("manifestHashes", out var manifestField) ||
                !fields.TryGetValue("segmentHashes", out var segmentField) ||
                !fields.TryGetValue("branchHashes", out var branchField))
                throw new InvalidDataException("The import ledger has an unrecognized schema or missing required fields.");
            if (fields.TryGetValue("schemaVersion", out var versionField) && (!versionField.TryGetInt32(out var version) || version != CurrentSchemaVersion))
                throw new InvalidDataException("The import ledger schema version is unsupported.");

            var ledger = new MediaImportLedger(ParseArray(manifestField, requireSha256: true), ParseArray(segmentField, requireSha256: true), ParseArray(branchField, requireSha256: false));
            ValidateLedger(ledger);
            return ledger;
        }
    }

    private static IReadOnlySet<string> ParseArray(JsonElement value, bool requireSha256)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("An import ledger field is not an array.");
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException("An import ledger array contains a non-string value.");
            var item = element.GetString();
            if (string.IsNullOrWhiteSpace(item) || item.Length > 128 || item.Any(char.IsControl) ||
                (requireSha256 && (item.Length != 64 || !item.All(Uri.IsHexDigit))))
                throw new InvalidDataException("An import ledger contains a malformed identity value.");
            values.Add(item);
        }

        return values;
    }

    private static void ValidateLedger(MediaImportLedger ledger)
    {
        ValidateHashes(ledger.ManifestHashes);
        ValidateHashes(ledger.SegmentHashes);
        foreach (var branch in ledger.BranchHashes)
        {
            if (string.IsNullOrWhiteSpace(branch) || branch.Length > 128 || branch.Any(char.IsControl))
                throw new InvalidDataException("The import ledger contains a malformed branch identity.");
        }
    }

    private static void ValidateHashes(IEnumerable<string> hashes)
    {
        foreach (var hash in hashes)
        {
            if (string.IsNullOrWhiteSpace(hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("The import ledger contains a malformed SHA-256 identity.");
        }
    }
}
