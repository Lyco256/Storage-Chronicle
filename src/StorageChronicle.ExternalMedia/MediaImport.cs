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
    private sealed record LedgerDocument(string[] ManifestHashes, string[] SegmentHashes, string[] BranchHashes);
    private sealed record LedgerOwnershipMarker(string Schema);
    private const string OwnershipMarkerName = ".storage-chronicle-ledgers-owner.json";
    private const string OwnershipSchema = "StorageChronicle.MediaLedgerOwnership.v1";
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
        ValidateOwnedLedgerDirectory(createIfMissing: false);
        EnsureNotReparsePoint(path);
        try
        {
            var document = JsonSerializer.Deserialize<LedgerDocument>(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), Options);
            if (document is null) throw new InvalidDataException("The media import ledger is empty; its original bytes were retained.");
            return new MediaImportLedger((document.ManifestHashes ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase), (document.SegmentHashes ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase), (document.BranchHashes ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The media import ledger is corrupt; its original bytes were retained and import was stopped.", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidDataException("The media import ledger could not be read safely; its contents were retained and import was stopped.", exception);
        }
    }

    /// <summary>Saves the ledger atomically so a process stop cannot erase prior deduplication.</summary>
    public async ValueTask SaveAsync(MediaImportLedger ledger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) throw new IOException("The media import ledger has no parent directory.");
        ValidateOwnedLedgerDirectory(createIfMissing: true);
        var current = File.Exists(path) ? await LoadAsync(cancellationToken).ConfigureAwait(false) : MediaImportLedger.Empty;
        if (!ledger.ManifestHashes.IsSupersetOf(current.ManifestHashes) || !ledger.SegmentHashes.IsSupersetOf(current.SegmentHashes) || !ledger.BranchHashes.IsSupersetOf(current.BranchHashes))
        {
            throw new InvalidOperationException("A media import ledger save cannot discard previously recorded hashes.");
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var document = new LedgerDocument(ledger.ManifestHashes.Order(StringComparer.OrdinalIgnoreCase).ToArray(), ledger.SegmentHashes.Order(StringComparer.OrdinalIgnoreCase).ToArray(), ledger.BranchHashes.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, true);
    }

    private void ValidateOwnedLedgerDirectory(bool createIfMissing)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new IOException("The media import ledger has no parent directory.");
        EnsureNoReparsePoints(directory);
        if (!Directory.Exists(directory))
        {
            if (!createIfMissing) return;
            Directory.CreateDirectory(directory);
            EnsureNoReparsePoints(directory);
            var newMarker = Path.Combine(directory, OwnershipMarkerName);
            using var output = new FileStream(newMarker, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1024, FileOptions.WriteThrough);
            JsonSerializer.Serialize(output, new LedgerOwnershipMarker(OwnershipSchema), Options);
            output.Flush(flushToDisk: true);
            return;
        }

        var markerPath = Path.Combine(directory, OwnershipMarkerName);
        EnsureNotReparsePoint(markerPath);
        if (!File.Exists(markerPath)) throw new IOException("The existing media ledger directory is not marked as Storage Chronicle-owned and will not be changed.");
        using (var input = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var marker = JsonSerializer.Deserialize<LedgerOwnershipMarker>(input, Options);
            if (marker?.Schema != OwnershipSchema) throw new IOException("The media ledger ownership marker is invalid.");
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            EnsureNotReparsePoint(entry);
            if (string.Equals(Path.GetFileName(entry), OwnershipMarkerName, StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(entry)) throw new IOException("An unexpected directory exists in the media ledger store; it will not be changed.");
            var name = Path.GetFileName(entry);
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !IsOwnedTemporaryLedgerName(name))
            {
                throw new IOException("An unexpected file exists in the media ledger store; it will not be changed.");
            }
        }
    }

    private bool IsOwnedTemporaryLedgerName(string name)
    {
        var prefix = Path.GetFileName(path) + ".";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParseExact(name.AsSpan(prefix.Length, name.Length - prefix.Length - ".tmp".Length), "N", out _);
    }

    private static void EnsureNoReparsePoints(string path)
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
