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
                }
                catch (InvalidDataException) { warnings.Add(MediaImportWarning.SegmentCorrupt); }
                catch (FileNotFoundException) { warnings.Add(MediaImportWarning.SegmentCorrupt); }
            }
        }

        return new MediaImportResult(events, importedManifests, duplicates, branch, warnings.Contains(MediaImportWarning.SegmentCorrupt) ? MediaHistoryQuality.UnverifiedGap : MediaHistoryQuality.Exact, warnings.OrderBy(value => value).ToArray());
    }
}

/// <summary>Persists media import deduplication hashes on the PC side.</summary>
public sealed class MediaImportLedgerStore
{
    private sealed record LedgerDocument(string[] ManifestHashes, string[] SegmentHashes, string[] BranchHashes);
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = false };
    private readonly string path;

    /// <summary>Creates a ledger store at a PC-side path.</summary>
    public MediaImportLedgerStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    /// <summary>Loads the ledger or returns an empty ledger when no ledger exists.</summary>
    public async ValueTask<MediaImportLedger> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return MediaImportLedger.Empty;
        var document = JsonSerializer.Deserialize<LedgerDocument>(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), Options);
        return document is null
            ? MediaImportLedger.Empty
            : new MediaImportLedger((document.ManifestHashes ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase), (document.SegmentHashes ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase), (document.BranchHashes ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Saves the ledger atomically so a process stop cannot erase prior deduplication.</summary>
    public async ValueTask SaveAsync(MediaImportLedger ledger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        var document = new LedgerDocument(ledger.ManifestHashes.Order(StringComparer.OrdinalIgnoreCase).ToArray(), ledger.SegmentHashes.Order(StringComparer.OrdinalIgnoreCase).ToArray(), ledger.BranchHashes.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(document, Options), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, true);
    }
}
