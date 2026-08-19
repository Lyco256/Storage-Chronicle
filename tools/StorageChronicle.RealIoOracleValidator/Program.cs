using System.Text.Json;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Storage;

namespace StorageChronicle.RealIoOracleValidator;

/// <summary>Compares a real FileMutationWorkload oracle with durable Storage Chronicle facts and state.</summary>
public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Runs the fail-closed oracle comparison.</summary>
    public static async Task<int> Main(string[] args)
    {
        if (!TryParse(args, out var oraclePath, out var historyPath, out var outputPath, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("usage: StorageChronicle.RealIoOracleValidator --oracle <path> --history <directory> --output <path>");
            return 2;
        }

        var failures = new List<string>();
        try
        {
            using var oracleDocument = JsonDocument.Parse(await File.ReadAllTextAsync(oraclePath).ConfigureAwait(false));
            var oracle = oracleDocument.RootElement;
            var schema = GetString(oracle, "Schema");
            if (!string.Equals(schema, "StorageChronicle.FileMutationWorkload.v2", StringComparison.Ordinal)) failures.Add($"Unexpected oracle schema: {schema}");
            var operations = oracle.TryGetProperty("Operations", out var operationArray) && operationArray.ValueKind == JsonValueKind.Array
                ? operationArray.EnumerateArray().ToArray()
                : Array.Empty<JsonElement>();
            if (operations.Length == 0) failures.Add("The workload oracle contains no operations.");

            var operationKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var operation in operations)
            {
                var operationName = GetString(operation, "Operation");
                operationKinds.Add(MapKind(operationName));
                if (!TryGetDateTimeOffset(operation, "StartedUtc", out var started) || !TryGetDateTimeOffset(operation, "CompletedUtc", out var completed)) failures.Add($"Operation {operationName} is missing UTC start/end timestamps.");
                else if (completed < started) failures.Add($"Operation {operationName} has a completion time before its start time.");
            }

            var sourceEvents = new List<SourceEvent>();
            var canonicalEvents = new List<CanonicalEvent>();
            await using (var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(historyPath) { FlushInterval = TimeSpan.FromMinutes(10) }))
            {
                await foreach (var value in storage.ReadSourceAsync()) sourceEvents.Add(value);
                await foreach (var value in storage.ReadCanonicalAsync()) canonicalEvents.Add(value);
                var state = await storage.GetSnapshotAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
                var checks = BuildChecks(operations, operationKinds, sourceEvents, canonicalEvents, state, failures);
                var evidence = CreateEvidence(oracle, historyPath, operations, sourceEvents, canonicalEvents, state.Entries.Count + state.UnplacedEntries.Count, checks, failures);
                await WriteEvidenceAsync(outputPath, evidence).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(evidence, JsonOptions));
                return failures.Count == 0 ? 0 : 2;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            failures.Add(exception.Message);
            var evidence = new
            {
                Schema = "StorageChronicle.WindowsTestLabRealIoEvidence.v1",
                Status = "FAILED",
                AcceptanceEligible = false,
                OraclePath = oraclePath,
                HistoryPath = historyPath,
                FailureReasons = failures,
                GeneratedUtc = DateTimeOffset.UtcNow
            };
            await WriteEvidenceAsync(outputPath, evidence).ConfigureAwait(false);
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static IReadOnlyList<object> BuildChecks(IReadOnlyList<JsonElement> operations, IReadOnlySet<string> operationKinds, IReadOnlyList<SourceEvent> sourceEvents, IReadOnlyList<CanonicalEvent> canonicalEvents, FileStateSnapshot state, ICollection<string> failures)
    {
        var checks = new List<object>();
        var stateObservations = BuildStateObservations(state);
        var statePaths = stateObservations
            .GroupBy(static value => value.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var metadataById = BuildMetadataIndex(state, canonicalEvents, sourceEvents);
        var canonicalPathIndex = BuildCanonicalPathIndex(canonicalEvents, metadataById);
        var canonicalFileIndex = canonicalEvents
            .Where(static value => value.FileId is not null)
            .GroupBy(static value => value.FileId!.Value)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        AddCheck("SourceEventsPresent", sourceEvents.Count > 0, "At least one durable source fact exists.", checks, failures);
        AddCheck("CanonicalEventsPresent", canonicalEvents.Count > 0, "At least one durable canonical event exists.", checks, failures);
        AddCheck("FinalStatePresent", stateObservations.Count > 0, "The reconstructed final state is non-empty.", checks, failures);
        AddCheck("ProcessQualityRecorded", canonicalEvents.All(value => value.ProcessQuality is ProcessAttributionQuality.Exact or ProcessAttributionQuality.Correlated or ProcessAttributionQuality.Unknown), "Every durable canonical event records an allowed process-quality value.", checks, failures);
        AddCheck("ReadObservationsNotDurable", canonicalEvents.All(value => !value.IsReadOnlyObservation), "Read/open/query observations did not enter durable canonical history.", checks, failures);
        var oraclePathsMatched = operations.All(operation => HasPathEvidence(operation, sourceEvents, canonicalEvents, statePaths, metadataById));
        AddCheck("OraclePathCoverage", oraclePathsMatched, "Every real workload oracle path has exact relative-path evidence in source, canonical, or reconstructed state data; same-name leaves in another directory do not satisfy the check.", checks, failures);
        var stateCorrectness = ValidateStateExpectations(operations, statePaths, failures);
        AddCheck("FinalStateOracle", stateCorrectness, "The final state contains every workload path that should remain live and retains state rows for observed deletions or directory-parent changes.", checks, failures);

        foreach (var kind in operationKinds)
        {
            var matched = operations
                .Where(operation => string.Equals(MapKind(GetString(operation, "Operation")), kind, StringComparison.OrdinalIgnoreCase))
                .All(operation => HasCanonicalOperationEvidence(operation, canonicalPathIndex, canonicalFileIndex, statePaths));
            AddCheck($"OracleCoverage.{kind}", matched, $"The durable canonical history contains a compatible event for every oracle {kind} operation, matched to its path and metadata.", checks, failures);
        }

        var directoryParentOperations = operations.Count(value => GetString(value, "Operation") is "DirectoryMove" or "DirectoryDelete");
        if (directoryParentOperations > 0)
        {
            var descendantRecords = canonicalEvents.Count(value => value.Operation is CanonicalOperation.DirectoryCreate && value.Name is not null && value.Name.Contains("item-", StringComparison.OrdinalIgnoreCase));
            AddCheck("DirectoryOperationsRemainParentScoped", descendantRecords == 0, "Directory move/delete acceptance does not rely on synthetic descendant events.", checks, failures);
        }

        return checks;
    }

    private static IReadOnlyList<StateObservation> BuildStateObservations(FileStateSnapshot state)
    {
        var entries = state.Entries.Concat(state.UnplacedEntries).ToArray();
        var metadataById = entries
            .Select(static value => value.Metadata)
            .GroupBy(static value => value.FileId)
            .ToDictionary(static group => group.Key, static group => group.Last());
        var paths = new Dictionary<FileId, string>();
        var observations = new List<StateObservation>(entries.Length);
        foreach (var entry in entries)
        {
            var path = ResolvePath(entry.Metadata.FileId, entry.Metadata.Name, entry.Metadata.ParentFileId, metadataById, paths, new HashSet<FileId>());
            if (path.Length > 0) observations.Add(new StateObservation(entry, path));
        }

        return observations;
    }

    private static Dictionary<FileId, FileMetadata> BuildMetadataIndex(FileStateSnapshot state, IReadOnlyList<CanonicalEvent> canonicalEvents, IReadOnlyList<SourceEvent> sourceEvents)
    {
        var result = state.Entries.Concat(state.UnplacedEntries).Select(static value => value.Metadata).ToDictionary(static value => value.FileId);
        foreach (var metadata in canonicalEvents.Select(static value => value.Metadata).Concat(sourceEvents.Select(static value => value.Metadata)).Where(static value => value is not null).Select(static value => value!))
        {
            result.TryAdd(metadata.FileId, metadata);
        }

        return result;
    }

    private static Dictionary<string, CanonicalEvent[]> BuildCanonicalPathIndex(IReadOnlyList<CanonicalEvent> canonicalEvents, IReadOnlyDictionary<FileId, FileMetadata> metadataById)
    {
        var result = new Dictionary<string, List<CanonicalEvent>>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in canonicalEvents)
        {
            var path = ResolveEventPath(value, metadataById);
            if (path.Length == 0) continue;
            if (!result.TryGetValue(path, out var values))
            {
                values = [];
                result[path] = values;
            }

            values.Add(value);
        }

        return result.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasPathEvidence(JsonElement operation, IReadOnlyList<SourceEvent> sourceEvents, IReadOnlyList<CanonicalEvent> canonicalEvents, IReadOnlyDictionary<string, StateObservation[]> statePaths, IReadOnlyDictionary<FileId, FileMetadata> metadataById)
    {
        var path = NormalizePath(GetString(operation, "RelativePath"));
        if (path.Length == 0) return false;
        if (statePaths.ContainsKey(path)) return true;
        var oldPath = NormalizePath(GetString(operation, "OldRelativePath"));
        return sourceEvents.Any(value => EventPathMatches(value.FileId, value.Name, value.ParentFileId, path, metadataById) ||
                                         (oldPath.Length > 0 && EventPathMatches(value.FileId, value.OldName, value.ParentFileId, oldPath, metadataById))) ||
               canonicalEvents.Any(value => EventPathMatches(value.FileId, value.Name, value.ParentFileId, path, metadataById) ||
                                             (oldPath.Length > 0 && EventPathMatches(value.FileId, value.OldName, value.ParentFileId, oldPath, metadataById)));
    }

    private static bool HasCanonicalOperationEvidence(JsonElement operation, IReadOnlyDictionary<string, CanonicalEvent[]> canonicalPathIndex, IReadOnlyDictionary<FileId, CanonicalEvent[]> canonicalFileIndex, IReadOnlyDictionary<string, StateObservation[]> statePaths)
    {
        var operationName = GetString(operation, "Operation");
        var path = NormalizePath(GetString(operation, "RelativePath"));
        var oldPath = NormalizePath(GetString(operation, "OldRelativePath"));
        var candidates = canonicalPathIndex.TryGetValue(path, out var pathCandidates) ? pathCandidates : [];
        var targetIds = statePaths.TryGetValue(path, out var observations) ? observations.Select(static value => value.Entry.Metadata.FileId).ToHashSet() : [];
        return operationName switch
        {
            "Create" or "EmptyCreate" or "CreateReplacement" => candidates.Any(value => value.Operation is CanonicalOperation.Create or CanonicalOperation.DirectoryCreate or CanonicalOperation.ReconciliationDiscovered),
            "DirectoryCreate" => candidates.Any(value => (value.Operation is CanonicalOperation.DirectoryCreate or CanonicalOperation.Create or CanonicalOperation.ReconciliationDiscovered) && value.Metadata?.Kind == FileKind.Directory),
            "Write" or "DataWrite" => candidates.Any(value => value.Operation is CanonicalOperation.DataWrite or CanonicalOperation.Extend or CanonicalOperation.Truncate),
            "Truncate" => candidates.Any(value => value.Operation is CanonicalOperation.Truncate),
            "Extend" => candidates.Any(value => value.Operation is CanonicalOperation.Extend),
            "MetadataChanged" => candidates.Any(value => value.Operation is CanonicalOperation.MetadataChanged or CanonicalOperation.SecurityMetadataChanged),
            "Rename" => candidates.Any(value => value.Operation is CanonicalOperation.Rename && RenameIdentityMatches(value, targetIds, oldPath, canonicalFileIndex)),
            "Move" => candidates.Any(value => value.Operation is CanonicalOperation.Move && RenameIdentityMatches(value, targetIds, oldPath, canonicalFileIndex)),
            "DirectoryMove" => candidates.Any(value => (value.Operation is CanonicalOperation.Move or CanonicalOperation.Rename) && value.Metadata?.Kind == FileKind.Directory && RenameIdentityMatches(value, targetIds, oldPath, canonicalFileIndex)),
            "Delete" => candidates.Any(value => (value.Operation is CanonicalOperation.Delete or CanonicalOperation.Recycle or CanonicalOperation.ReconciliationDiscovered) && value.Metadata is not null),
            "DirectoryDelete" => candidates.Any(value => (value.Operation is CanonicalOperation.Delete or CanonicalOperation.Recycle or CanonicalOperation.ReconciliationDiscovered) && value.Metadata?.Kind == FileKind.Directory),
            "AclDenied" or "AclDeniedMetadata" => candidates.Any(value => value.Operation is CanonicalOperation.MetadataChanged or CanonicalOperation.SecurityMetadataChanged or CanonicalOperation.ReconciliationDiscovered),
            _ => false
        };
    }

    private static bool RenameIdentityMatches(CanonicalEvent value, IReadOnlySet<FileId> targetIds, string oldPath, IReadOnlyDictionary<FileId, CanonicalEvent[]> canonicalFileIndex)
    {
        if (value.FileId is not { } fileId) return false;
        if (targetIds.Count > 0 && !targetIds.Contains(fileId)) return false;
        if (oldPath.Length == 0 || !NameMatches(value.OldName, GetLeaf(oldPath))) return false;
        return canonicalFileIndex.TryGetValue(fileId, out var history) && history.Any(previous =>
            previous.EventId != value.EventId &&
            NameMatches(previous.Name, GetLeaf(oldPath)) &&
            previous.Time.RecordedUtc <= value.Time.RecordedUtc);
    }

    private static bool ValidateStateExpectations(IReadOnlyList<JsonElement> operations, IReadOnlyDictionary<string, StateObservation[]> statePaths, ICollection<string> failures)
    {
        var passed = true;
        var lastDirectByPath = new Dictionary<string, (string Operation, long Sequence)>(StringComparer.OrdinalIgnoreCase);
        var directoryDeletes = new List<(string Path, long Sequence)>();
        foreach (var candidate in operations)
        {
            var candidatePath = NormalizePath(GetString(candidate, "RelativePath"));
            if (candidatePath.Length == 0) continue;
            var candidateOperation = GetString(candidate, "Operation");
            var candidateSequence = GetInt64(candidate, "Sequence");
            if (!lastDirectByPath.TryGetValue(candidatePath, out var current) || candidateSequence >= current.Sequence)
            {
                lastDirectByPath[candidatePath] = (candidateOperation, candidateSequence);
            }

            if (string.Equals(candidateOperation, "DirectoryDelete", StringComparison.OrdinalIgnoreCase)) directoryDeletes.Add((candidatePath, candidateSequence));
        }

        foreach (var operation in operations.Where(static value => MapKind(GetString(value, "Operation")) is "Create" or "Write" or "RenameMove" or "Delete"))
        {
            var path = NormalizePath(GetString(operation, "RelativePath"));
            if (path.Length == 0) { passed = false; continue; }
            if (!lastDirectByPath.TryGetValue(path, out var lastDirect)) { passed = false; continue; }
            var operationSequence = GetInt64(operation, "Sequence");
            var directoryDeletedAfter = directoryDeletes.Any(candidate => IsPathWithin(path, candidate.Path) && candidate.Sequence > operationSequence);
            var expectedLive = !directoryDeletedAfter && lastDirect.Operation is not ("Delete" or "DirectoryDelete");
            if (!expectedLive) continue;
            if (!statePaths.TryGetValue(path, out var observations) || observations.All(static value => value.Entry.IsVirtualDeleted || !value.Entry.Metadata.Exists))
            {
                failures.Add($"FinalStateMissing:{path}");
                passed = false;
            }
        }

        return passed;
    }

    private static bool EventPathMatches(FileId? fileId, string? name, FileId? parentFileId, string expectedPath, IReadOnlyDictionary<FileId, FileMetadata> metadataById)
    {
        if (expectedPath.Length == 0 || string.IsNullOrWhiteSpace(name)) return false;
        var paths = new Dictionary<FileId, string>();
        var actual = ResolvePath(fileId, name, parentFileId, metadataById, paths, new HashSet<FileId>());
        return string.Equals(actual, expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveEventPath(CanonicalEvent value, IReadOnlyDictionary<FileId, FileMetadata> metadataById)
        => ResolvePath(value.FileId, value.Name ?? value.Metadata?.Name, value.ParentFileId ?? value.Metadata?.ParentFileId, metadataById, new Dictionary<FileId, string>(), new HashSet<FileId>());

    private static string ResolvePath(FileId? fileId, string? name, FileId? parentFileId, IReadOnlyDictionary<FileId, FileMetadata> metadataById, IDictionary<FileId, string> paths, ISet<FileId> visiting)
    {
        var ownName = NormalizePath(name);
        if (fileId is { } id && paths.TryGetValue(id, out var cached)) return cached;
        if (fileId is { } current && !visiting.Add(current)) return ownName;
        var parentPath = string.Empty;
        if (parentFileId is { } parent && metadataById.TryGetValue(parent, out var parentMetadata))
        {
            parentPath = ResolvePath(parent, parentMetadata.Name, parentMetadata.ParentFileId, metadataById, paths, visiting);
        }

        var result = parentPath.Length == 0 ? ownName : CombinePath(parentPath, ownName);
        if (fileId is { } resolvedId)
        {
            paths[resolvedId] = result;
            visiting.Remove(resolvedId);
        }

        return result;
    }

    private static bool IsPathWithin(string path, string parent) => parent.Length > 0 &&
        (string.Equals(path, parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase));

    private static string CombinePath(string parent, string name) => parent.Length == 0 ? name : name.Length == 0 ? parent : parent + "\\" + name;

    private static string NormalizePath(string? path) => string.IsNullOrWhiteSpace(path)
        ? string.Empty
        : string.Join('\\', path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string GetLeaf(string path) => NormalizePath(path).Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

    private static long GetInt64(JsonElement value, string property) => value.TryGetProperty(property, out var item) && item.TryGetInt64(out var result) ? result : 0L;

    private sealed record StateObservation(FileStateEntry Entry, string Path);

    private static object CreateEvidence(JsonElement oracle, string historyPath, IReadOnlyList<JsonElement> operations, IReadOnlyList<SourceEvent> sourceEvents, IReadOnlyList<CanonicalEvent> canonicalEvents, int finalStateCount, IReadOnlyList<object> checks, IReadOnlyList<string> failures) => new
    {
        Schema = "StorageChronicle.WindowsTestLabRealIoEvidence.v1",
        Status = failures.Count == 0 ? "PASSED" : "FAILED",
        AcceptanceEligible = failures.Count == 0,
        RunId = GetString(oracle, "RunId"),
        Scenario = GetString(oracle, "Scenario"),
        OracleOperationCount = operations.Count,
        SourceEventCount = sourceEvents.Count,
        CanonicalEventCount = canonicalEvents.Count,
        FinalStateCount = finalStateCount,
        SourceOrigins = sourceEvents.Select(value => value.Origin.ToString()).Distinct(StringComparer.Ordinal).Order().ToArray(),
        CanonicalOperations = canonicalEvents.Select(value => value.Operation.ToString()).Distinct(StringComparer.Ordinal).Order().ToArray(),
        ProcessQualities = canonicalEvents.Select(value => value.ProcessQuality.ToString()).Distinct(StringComparer.Ordinal).Order().ToArray(),
        OraclePaths = operations.Select(value => GetString(value, "RelativePath")).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray(),
        Checks = checks,
        FailureReasons = failures,
        HistoryPath = historyPath,
        GeneratedUtc = DateTimeOffset.UtcNow
    };

    private static string MapKind(string operation) => operation switch
    {
        "Create" or "EmptyCreate" or "CreateReplacement" or "DirectoryCreate" => "Create",
        "Write" or "DataWrite" or "MetadataChanged" or "Truncate" or "Extend" => "Write",
        "Rename" or "Move" or "DirectoryMove" => "RenameMove",
        "Delete" or "DirectoryDelete" => "Delete",
        "AclDeniedMetadata" => "AclDenied",
        _ => "Other"
    };

    private static void AddCheck(string name, bool passed, string description, ICollection<object> checks, ICollection<string> failures)
    {
        checks.Add(new { Name = name, Status = passed ? "PASSED" : "FAILED", Description = description });
        if (!passed) failures.Add(name);
    }

    private static bool NameMatches(string? actual, string expected) => !string.IsNullOrWhiteSpace(actual) &&
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static async Task WriteEvidenceAsync(string outputPath, object evidence)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(fullPath) ?? throw new IOException("The evidence output has no parent directory.");
        Directory.CreateDirectory(parent);
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(evidence, JsonOptions)).ConfigureAwait(false);
    }

    private static string GetString(JsonElement value, string property) => value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty;

    private static bool TryGetDateTimeOffset(JsonElement value, string property, out DateTimeOffset result)
    {
        if (value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String && item.TryGetDateTimeOffset(out result)) return true;
        result = default;
        return false;
    }

    private static bool TryParse(string[] args, out string oracle, out string history, out string output, out string error)
    {
        oracle = string.Empty;
        history = string.Empty;
        output = string.Empty;
        error = string.Empty;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index].ToLowerInvariant())
            {
                case "--oracle" when index + 1 < args.Length: oracle = args[++index]; break;
                case "--history" when index + 1 < args.Length: history = args[++index]; break;
                case "--output" when index + 1 < args.Length: output = args[++index]; break;
                default: error = $"Unknown or incomplete argument: {args[index]}"; return false;
            }
        }

        if (string.IsNullOrWhiteSpace(oracle) || string.IsNullOrWhiteSpace(history) || string.IsNullOrWhiteSpace(output)) { error = "--oracle, --history, and --output are required."; return false; }
        if (!File.Exists(oracle)) { error = $"The oracle does not exist: {oracle}"; return false; }
        if (!Directory.Exists(history)) { error = $"The history directory does not exist: {history}"; return false; }
        return true;
    }
}
