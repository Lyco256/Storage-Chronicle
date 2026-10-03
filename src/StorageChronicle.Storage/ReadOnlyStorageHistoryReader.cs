using System.Text.Json;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Storage;

/// <summary>Reads the authoritative event segments without opening, creating, or repairing the SQLite index.</summary>
public sealed class ReadOnlyStorageHistoryReader
{
    private const string OwnershipMarkerName = ".storage-chronicle-history-owner.json";
    private const string OwnershipSchema = "StorageChronicle.HistoryOwnership.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    /// <summary>Reads events from a valid, already-existing product-owned history directory.</summary>
    /// <param name="storageDirectory">The existing absolute or relative history directory.</param>
    /// <param name="cancellationToken">A token observed while scanning event records.</param>
    /// <returns>The original events and any skipped-segment issues.</returns>
    /// <exception cref="IOException">The directory is missing, unowned, contains reparse points, or has unreadable segments.</exception>
    /// <exception cref="InvalidDataException">The ownership marker or an event payload is invalid.</exception>
    public ReadOnlyStorageHistorySnapshot Read(string storageDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        var fullPath = Path.GetFullPath(storageDirectory);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"The history directory does not exist: {fullPath}");
        EnsureNoReparsePoints(fullPath);
        ValidateOwnership(fullPath);
        AppendOnlyStorageEngine.ValidateOwnedHistoryEntries(fullPath);

        var options = new StorageEngineOptions(fullPath);
        var segmentLog = new SegmentLog(options, createDirectory: false);
        var issues = new List<SegmentIssue>();
        segmentLog.SegmentSkipped += issues.Add;
        var sourceEvents = new List<SourceEvent>();
        var canonicalEvents = new List<CanonicalEvent>();
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var record in segmentLog.ReadAllRecords())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (record.Kind)
            {
                case StorageRecordKind.SourceEvent:
                    sourceEvents.Add(JsonSerializer.Deserialize<SourceEvent>(record.Payload, JsonOptions)
                        ?? throw new InvalidDataException("A source event segment payload deserialized to null."));
                    break;
                case StorageRecordKind.CanonicalEvent:
                    canonicalEvents.Add(JsonSerializer.Deserialize<CanonicalEvent>(record.Payload, JsonOptions)
                        ?? throw new InvalidDataException("A canonical event segment payload deserialized to null."));
                    break;
            }
        }

        return new ReadOnlyStorageHistorySnapshot(sourceEvents, canonicalEvents, issues);
    }

    private static void ValidateOwnership(string directory)
    {
        var markerPath = Path.Combine(directory, OwnershipMarkerName);
        EnsureNoReparsePoints(markerPath);
        using var stream = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("Schema", out var schema) || schema.GetString() != OwnershipSchema)
            throw new InvalidDataException("The history ownership marker is invalid; the read-only reader will not adopt this directory.");
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"The history reader refuses paths that traverse reparse points: {current}");
            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            current = parent;
        }
    }
}

/// <summary>Contains events and segment issues observed without mutating the history directory.</summary>
/// <param name="SourceEvents">The readable source-event records.</param>
/// <param name="CanonicalEvents">The readable canonical-event records.</param>
/// <param name="Issues">Segments skipped because their framing, checksum, or compression was invalid.</param>
public sealed record ReadOnlyStorageHistorySnapshot(
    IReadOnlyList<SourceEvent> SourceEvents,
    IReadOnlyList<CanonicalEvent> CanonicalEvents,
    IReadOnlyList<SegmentIssue> Issues);
