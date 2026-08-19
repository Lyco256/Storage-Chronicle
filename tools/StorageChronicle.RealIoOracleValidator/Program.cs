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
                var checks = BuildChecks(operations, operationKinds, sourceEvents, canonicalEvents, state.Entries.Count + state.UnplacedEntries.Count, failures);
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

    private static IReadOnlyList<object> BuildChecks(IReadOnlyList<JsonElement> operations, IReadOnlySet<string> operationKinds, IReadOnlyList<SourceEvent> sourceEvents, IReadOnlyList<CanonicalEvent> canonicalEvents, int finalStateCount, ICollection<string> failures)
    {
        var checks = new List<object>();
        AddCheck("SourceEventsPresent", sourceEvents.Count > 0, "At least one durable source fact exists.", checks, failures);
        AddCheck("CanonicalEventsPresent", canonicalEvents.Count > 0, "At least one durable canonical event exists.", checks, failures);
        AddCheck("FinalStatePresent", finalStateCount > 0, "The reconstructed final state is non-empty.", checks, failures);
        AddCheck("ProcessQualityRecorded", canonicalEvents.All(value => value.ProcessQuality is ProcessAttributionQuality.Exact or ProcessAttributionQuality.Correlated or ProcessAttributionQuality.Unknown), "Every durable canonical event records an allowed process-quality value.", checks, failures);
        AddCheck("ReadObservationsNotDurable", canonicalEvents.All(value => !value.IsReadOnlyObservation), "Read/open/query observations did not enter durable canonical history.", checks, failures);
        var oraclePathsMatched = operations.All(operation =>
        {
            var path = GetString(operation, "RelativePath");
            if (string.IsNullOrWhiteSpace(path)) return false;
            var leaf = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? path;
            return sourceEvents.Any(value => NameMatches(value.Name, leaf) || NameMatches(value.OldName, leaf)) ||
                canonicalEvents.Any(value => NameMatches(value.Name, leaf) || NameMatches(value.OldName, leaf) || (value.Metadata is not null && NameMatches(value.Metadata.Name, leaf)));
        });
        AddCheck("OraclePathCoverage", oraclePathsMatched, "Every real workload oracle path has a matching source or canonical metadata name.", checks, failures);

        foreach (var kind in operationKinds)
        {
            var matched = kind switch
            {
                "Create" => canonicalEvents.Any(value => value.Operation is CanonicalOperation.Create or CanonicalOperation.DirectoryCreate or CanonicalOperation.ReconciliationDiscovered),
                "Write" => canonicalEvents.Any(value => value.Operation is CanonicalOperation.DataWrite or CanonicalOperation.MetadataChanged or CanonicalOperation.Truncate or CanonicalOperation.Extend or CanonicalOperation.ReconciliationDiscovered),
                "RenameMove" => canonicalEvents.Any(value => value.Operation is CanonicalOperation.Rename or CanonicalOperation.Move or CanonicalOperation.ReconciliationDiscovered),
                "Delete" => canonicalEvents.Any(value => value.Operation is CanonicalOperation.Delete or CanonicalOperation.Recycle or CanonicalOperation.ReconciliationDiscovered),
                "AclDenied" => canonicalEvents.Any(value => value.Operation is CanonicalOperation.ReconciliationDiscovered or CanonicalOperation.SecurityMetadataChanged or CanonicalOperation.MetadataChanged),
                _ => false
            };
            AddCheck($"OracleCoverage.{kind}", matched, $"The durable canonical history contains a compatible event for the oracle's {kind} operations.", checks, failures);
        }

        var directoryParentOperations = operations.Count(value => GetString(value, "Operation") is "DirectoryMove" or "DirectoryDelete");
        if (directoryParentOperations > 0)
        {
            var descendantRecords = canonicalEvents.Count(value => value.Operation is CanonicalOperation.DirectoryCreate && value.Name is not null && value.Name.Contains("item-", StringComparison.OrdinalIgnoreCase));
            AddCheck("DirectoryOperationsRemainParentScoped", descendantRecords == 0, "Directory move/delete acceptance does not rely on synthetic descendant events.", checks, failures);
        }

        return checks;
    }

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
