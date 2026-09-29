using System.Globalization;
using System.Text.Json;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Storage;

namespace StorageChronicle.LiveCorrelationValidator;

/// <summary>Validates real Agent/process/Explorer correlation evidence without accepting a fixture as live data.</summary>
public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>Reads a real workload oracle, durable Agent history, and Explorer scenario log into one strict evidence artifact.</summary>
    public static async Task<int> Main(string[] args)
    {
        if (!TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("usage: StorageChronicle.LiveCorrelationValidator --oracle <path> --history <directory> --explorer <path> --environment <path> --output <path>");
            return 2;
        }

        try
        {
            var oracle = Load<OracleDocument>(options.OraclePath, "workload oracle");
            var explorer = Load<ExplorerDocument>(options.ExplorerPath, "Explorer scenario evidence");
            var environment = Load<CorrelationEnvironment>(options.EnvironmentPath, "correlation environment");
            ValidateEnvironment(environment, options.HistoryPath, options.OraclePath, options.ExplorerPath);
            if (!string.Equals(oracle.Schema, "StorageChronicle.FileMutationWorkload.v2", StringComparison.Ordinal) || oracle.Operations.Count == 0)
            {
                throw new InvalidDataException("The workload oracle schema or operation list is invalid.");
            }
            if (!string.Equals(explorer.Schema, "StorageChronicle.ExplorerScenario.v1", StringComparison.Ordinal) || explorer.Rows.Count == 0)
            {
                throw new InvalidDataException("The Explorer scenario schema or row list is invalid.");
            }

            await using var storage = new AppendOnlyStorageEngine(new StorageEngineOptions(options.HistoryPath) { FlushInterval = TimeSpan.FromMinutes(1) });
            var sources = new List<SourceEvent>();
            var canonical = new List<CanonicalEvent>();
            await foreach (var value in storage.ReadSourceAsync().ConfigureAwait(false)) sources.Add(value);
            await foreach (var value in storage.ReadCanonicalAsync().ConfigureAwait(false)) canonical.Add(value);
            var snapshot = await storage.GetSnapshotAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
            var metadata = BuildMetadataIndex(snapshot, canonical, sources);
            var paths = BuildCanonicalPathIndex(canonical, metadata);
            var statePaths = BuildStatePathIndex(snapshot, metadata);
            var processMeasurement = MeasureProcessAttribution(oracle, oracle.Operations, paths);
            var correctness = MeasureFileStateCorrectness(oracle.Operations, paths, statePaths);
            var explorerMeasurement = MeasureExplorer(explorer, paths);
            var failures = new List<string>();
            if (processMeasurement.FalseExactCount > 0) failures.Add("FalseExactCount must be zero.");
            if (correctness.MissingCount != 0 || correctness.DroppedEventCount != 0) failures.Add("The workload oracle is not fully represented by canonical/state evidence.");
            if (explorerMeasurement.CopyIntentCount == 0) failures.Add("No real Explorer copy-intent scenario was supplied.");
            if (explorerMeasurement.FalseAttributionCount != 0) failures.Add("Explorer evidence contains a false source attribution.");

            var evidence = new
            {
                Schema = "StorageChronicle.AgentExplorerCorrelationEvidence.v1",
                Status = failures.Count == 0 ? "PASSED" : "FAILED",
                AcceptanceEligible = failures.Count == 0,
                LiveMachineMeasurement = failures.Count == 0 ? "PASSED" : "FAILED",
                FalseExactCount = processMeasurement.FalseExactCount,
                WorkloadOraclePath = Path.GetFullPath(options.OraclePath),
                ProcessAttribution = processMeasurement,
                ExplorerSourceCorrelation = explorerMeasurement,
                FileStateCorrectness = correctness,
                Environment = environment,
                SourceEventCount = sources.Count,
                CanonicalEventCount = canonical.Count,
                FinalStateCount = snapshot.Entries.Count + snapshot.UnplacedEntries.Count,
                Failures = failures,
                GeneratedUtc = DateTimeOffset.UtcNow
            };
            Write(options.OutputPath, evidence);
            Console.WriteLine(JsonSerializer.Serialize(evidence, JsonOptions));
            return failures.Count == 0 ? 0 : 2;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or InvalidOperationException or NullReferenceException)
        {
            var failure = new
            {
                Schema = "StorageChronicle.AgentExplorerCorrelationEvidence.v1",
                Status = "FAILED",
                AcceptanceEligible = false,
                LiveMachineMeasurement = "FAILED",
                FalseExactCount = -1,
                Failure = exception.Message,
                GeneratedUtc = DateTimeOffset.UtcNow
            };
            Write(options.OutputPath, failure);
            Console.Error.WriteLine($"FAIL_CLOSED: {exception.Message}");
            return 2;
        }
    }

    private static ProcessMeasurement MeasureProcessAttribution(OracleDocument oracle, IReadOnlyList<OracleOperation> operations, IReadOnlyDictionary<string, CanonicalEvent[]> paths)
    {
        var processes = (oracle.Processes ?? Array.Empty<ProcessEvidence>()).Append(oracle.Process).GroupBy(value => (value.ProcessId, value.StartTimeUtc)).Select(group => group.First()).ToArray();
        var rows = new List<object>(operations.Count);
        var exact = 0;
        var correlated = 0;
        var unknown = 0;
        var falseExact = 0;
        foreach (var operation in operations)
        {
            var candidates = GetOperationCandidates(operation, paths);
            var value = candidates.OrderByDescending(static item => item.ProcessQuality == ProcessAttributionQuality.Exact)
                .ThenByDescending(static item => item.Time.RecordedUtc).FirstOrDefault();
            var quality = value?.ProcessQuality ?? ProcessAttributionQuality.Unknown;
            var known = value?.ProcessInstanceId is { } process && IsKnownProcess(process, processes);
            if (quality == ProcessAttributionQuality.Exact)
            {
                exact++;
                if (!known) falseExact++;
            }
            else if (quality == ProcessAttributionQuality.Correlated) correlated++;
            else unknown++;

            rows.Add(new
            {
                operation.Sequence,
                operation.Operation,
                operation.RelativePath,
                EventId = value?.EventId.ToString(),
                ProcessInstanceId = value?.ProcessInstanceId?.Value,
                Quality = quality.ToString(),
                KnownOracleProcess = known
            });
        }

        return new ProcessMeasurement(operations.Count, exact, correlated, unknown, falseExact, Rate(exact, operations.Count), Rate(correlated, operations.Count), Rate(unknown, operations.Count), rows);
    }

    private static FileStateCorrectness MeasureFileStateCorrectness(IReadOnlyList<OracleOperation> operations, IReadOnlyDictionary<string, CanonicalEvent[]> paths, IReadOnlyDictionary<string, FileStateEntry[]> statePaths)
    {
        var last = operations.GroupBy(value => NormalizePath(value.RelativePath), StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.OrderBy(value => value.Sequence).Last(), StringComparer.OrdinalIgnoreCase);
        var verified = 0;
        var missing = 0;
        foreach (var operation in operations)
        {
            var path = NormalizePath(operation.RelativePath);
            var candidates = GetOperationCandidates(operation, paths);
            var hasState = statePaths.TryGetValue(path, out var state);
            var finalOperation = last[path].Operation;
            var isDelete = finalOperation is "Delete" or "DirectoryDelete";
            var stateMatches = isDelete
                ? hasState && state!.Any(value => value.IsVirtualDeleted || !value.Metadata.Exists)
                : hasState && state!.Any(value => !value.IsVirtualDeleted && value.Metadata.Exists);
            var evidence = candidates.Count > 0 && stateMatches;
            if (evidence) verified++;
            else missing++;
        }

        return new FileStateCorrectness(operations.Count, verified, missing, missing, operations.Select(value => value.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static ExplorerMeasurement MeasureExplorer(ExplorerDocument document, IReadOnlyDictionary<string, CanonicalEvent[]> paths)
    {
        var rows = new List<object>(document.Rows.Count);
        var correlated = 0;
        var sourceUnknown = 0;
        var notIdentified = 0;
        var falseAttribution = 0;
        foreach (var scenario in document.Rows)
        {
            var candidates = paths.TryGetValue(NormalizePath(scenario.DestinationRelativePath), out var values) ? values : Array.Empty<CanonicalEvent>();
            var explorerEvent = candidates.FirstOrDefault(value => IsExplorer(value));
            var quality = GetProperty(explorerEvent?.Properties, "copyCorrelationQuality");
            var sourceId = GetProperty(explorerEvent?.Properties, "copySourceFileId");
            var rowQuality = string.Equals(quality, EventQuality.Correlated.ToString(), StringComparison.OrdinalIgnoreCase) ? "Correlated" : explorerEvent is null ? "NotIdentified" : "SourceUnknown";
            if (rowQuality == "Correlated") correlated++;
            else if (rowQuality == "SourceUnknown") sourceUnknown++;
            else notIdentified++;
            if (!string.IsNullOrWhiteSpace(sourceId) && rowQuality != "Correlated") falseAttribution++;
            if (rowQuality == "Correlated" && !string.IsNullOrWhiteSpace(scenario.ExpectedSourceFileId) && !string.Equals(sourceId, scenario.ExpectedSourceFileId, StringComparison.OrdinalIgnoreCase)) falseAttribution++;
            if (!string.IsNullOrWhiteSpace(scenario.ExpectedCorrelation) && !ExpectedCorrelationMatches(scenario.ExpectedCorrelation, rowQuality)) falseAttribution++;
            rows.Add(new
            {
                scenario.ScenarioId,
                scenario.Operation,
                scenario.DestinationRelativePath,
                scenario.SourceRelativePath,
                EventId = explorerEvent?.EventId.ToString(),
                ExplorerExecutable = GetProperty(explorerEvent?.Properties, "process.executable"),
                ClipboardGeneration = GetProperty(explorerEvent?.Properties, "clipboardGeneration"),
                SourceFileId = sourceId,
                Correlation = rowQuality
            });
        }

        return new ExplorerMeasurement(document.Rows.Count, correlated, sourceUnknown, notIdentified, falseAttribution, rows);
    }

    private static bool ExpectedCorrelationMatches(string expected, string actual)
    {
        var normalized = expected.Trim();
        return actual switch
        {
            "Correlated" => string.Equals(normalized, "Correlated", StringComparison.OrdinalIgnoreCase),
            "SourceUnknown" => normalized.Equals("SourceUnknown", StringComparison.OrdinalIgnoreCase) || normalized.Equals("Uncorrelated", StringComparison.OrdinalIgnoreCase),
            "NotIdentified" => normalized.Equals("NotIdentified", StringComparison.OrdinalIgnoreCase) || normalized.Equals("ExcludedNonExplorer", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool IsExplorer(CanonicalEvent value) => GetProperty(value.Properties, "process.executable")?.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase) == true || GetProperty(value.Properties, "process.name")?.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsKnownProcess(ProcessInstanceId id, IReadOnlyList<ProcessEvidence> processes)
    {
        var parts = id.Value.Split(':', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId) && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && processes.Any(value => value.ProcessId == processId && value.StartTimeUtc.Ticks == ticks);
    }

    private static IReadOnlyList<CanonicalEvent> GetOperationCandidates(OracleOperation operation, IReadOnlyDictionary<string, CanonicalEvent[]> paths)
    {
        var result = new List<CanonicalEvent>();
        AddCandidates(paths, NormalizePath(operation.RelativePath), result);
        if (!string.IsNullOrWhiteSpace(operation.OldRelativePath)) AddCandidates(paths, NormalizePath(operation.OldRelativePath), result);
        return result.DistinctBy(static value => value.EventId).Where(value => IsCompatible(operation.Operation, value)).ToArray();
    }

    private static void AddCandidates(IReadOnlyDictionary<string, CanonicalEvent[]> paths, string path, ICollection<CanonicalEvent> target)
    {
        if (paths.TryGetValue(path, out var values)) foreach (var value in values) target.Add(value);
    }

    private static bool IsCompatible(string operation, CanonicalEvent value) => operation switch
    {
        "Create" or "EmptyCreate" or "CreateReplacement" => value.Operation is CanonicalOperation.Create or CanonicalOperation.DirectoryCreate or CanonicalOperation.ReconciliationDiscovered,
        "DirectoryCreate" => value.Operation is CanonicalOperation.Create or CanonicalOperation.DirectoryCreate or CanonicalOperation.ReconciliationDiscovered,
        "Write" or "DataWrite" => value.Operation is CanonicalOperation.DataWrite or CanonicalOperation.Extend or CanonicalOperation.Truncate,
        "Truncate" => value.Operation == CanonicalOperation.Truncate,
        "Extend" => value.Operation == CanonicalOperation.Extend,
        "MetadataChanged" or "AclDeniedMetadata" => value.Operation is CanonicalOperation.MetadataChanged or CanonicalOperation.SecurityMetadataChanged or CanonicalOperation.ReconciliationDiscovered,
        "Rename" or "Move" or "DirectoryMove" => value.Operation is CanonicalOperation.Rename or CanonicalOperation.Move or CanonicalOperation.ReconciliationDiscovered,
        "Delete" or "DirectoryDelete" => value.Operation is CanonicalOperation.Delete or CanonicalOperation.Recycle or CanonicalOperation.ReconciliationDiscovered,
        _ => false
    };

    private static Dictionary<FileId, FileMetadata> BuildMetadataIndex(FileStateSnapshot snapshot, IReadOnlyList<CanonicalEvent> canonical, IReadOnlyList<SourceEvent> sources)
    {
        var result = snapshot.Entries.Concat(snapshot.UnplacedEntries).Select(static value => value.Metadata).GroupBy(static value => value.FileId).ToDictionary(static group => group.Key, static group => group.Last());
        foreach (var metadata in canonical.Select(static value => value.Metadata).Concat(sources.Select(static value => value.Metadata)).Where(static value => value is not null).Select(static value => value!)) result.TryAdd(metadata.FileId, metadata);
        return result;
    }

    private static Dictionary<string, CanonicalEvent[]> BuildCanonicalPathIndex(IReadOnlyList<CanonicalEvent> canonical, IReadOnlyDictionary<FileId, FileMetadata> metadata)
    {
        var result = new Dictionary<string, List<CanonicalEvent>>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in canonical)
        {
            AddPath(result, ResolvePath(value.FileId, value.Name ?? value.Metadata?.Name, value.ParentFileId ?? value.Metadata?.ParentFileId, metadata), value);
            if (!string.IsNullOrWhiteSpace(value.OldName)) AddPath(result, ResolvePath(value.FileId, value.OldName, value.ParentFileId ?? value.Metadata?.ParentFileId, metadata), value);
        }
        return result.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, FileStateEntry[]> BuildStatePathIndex(FileStateSnapshot snapshot, IReadOnlyDictionary<FileId, FileMetadata> metadata)
    {
        var result = new Dictionary<string, List<FileStateEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in snapshot.Entries.Concat(snapshot.UnplacedEntries))
        {
            var path = NormalizePath(entry.ReconstructedPath);
            if (path.Length == 0) path = ResolvePath(entry.Metadata.FileId, entry.Metadata.Name, entry.Metadata.ParentFileId, metadata);
            if (path.Length == 0) continue;
            if (!result.TryGetValue(path, out var values)) { values = []; result[path] = values; }
            values.Add(entry);
        }
        return result.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static void AddPath(IDictionary<string, List<CanonicalEvent>> index, string path, CanonicalEvent value)
    {
        if (path.Length == 0) return;
        if (!index.TryGetValue(path, out var values)) { values = []; index[path] = values; }
        values.Add(value);
    }

    private static string ResolvePath(FileId? fileId, string? name, FileId? parent, IReadOnlyDictionary<FileId, FileMetadata> metadata, IDictionary<FileId, string>? cache = null, ISet<FileId>? visiting = null)
    {
        cache ??= new Dictionary<FileId, string>();
        visiting ??= new HashSet<FileId>();
        var own = NormalizePath(name);
        if (fileId is { } id && cache.TryGetValue(id, out var cached)) return cached;
        if (fileId is { } current && !visiting.Add(current)) return own;
        var parentPath = parent is { } parentId && metadata.TryGetValue(parentId, out var parentMetadata) ? ResolvePath(parentId, parentMetadata.Name, parentMetadata.ParentFileId, metadata, cache, visiting) : string.Empty;
        var result = parentPath.Length == 0 ? own : parentPath + "\\" + own;
        if (fileId is { } resolved) { cache[resolved] = result; visiting.Remove(resolved); }
        return result;
    }

    private static string? GetProperty(IReadOnlyDictionary<string, string>? properties, string name)
        => properties?.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string NormalizePath(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : string.Join('\\', value.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static decimal Rate(int numerator, int denominator) => denominator == 0 ? 0m : decimal.Round((decimal)numerator / denominator, 4, MidpointRounding.AwayFromZero);

    private static T Load<T>(string path, string label)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"The {label} does not exist.", path);
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException($"The {label} is empty.");
    }

    private static void ValidateEnvironment(CorrelationEnvironment environment, string history, string oracle, string explorer)
    {
        if (!string.Equals(environment.TargetOs, "Windows11", StringComparison.Ordinal) || !string.Equals(environment.VmName, "SC-Test-W11-VBox", StringComparison.Ordinal) || !string.Equals(environment.ExecutionMode, "TestLab", StringComparison.Ordinal) || !string.Equals(environment.AgentHostMode, "TestLab", StringComparison.Ordinal) || environment.Diagnostic) throw new InvalidDataException("The correlation environment is not a non-diagnostic SC-Test-W11-VBox TestLab run.");
        if (!File.Exists(history) && !Directory.Exists(history)) throw new DirectoryNotFoundException($"Agent history does not exist: {history}");
        if (!Path.GetFullPath(environment.AgentHistoryPath ?? string.Empty).Equals(Path.GetFullPath(history), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Agent history path does not match the validator input.");
        if (!Path.GetFullPath(environment.WorkloadOraclePath ?? string.Empty).Equals(Path.GetFullPath(oracle), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Workload oracle path does not match the validator input.");
        if (!Path.GetFullPath(environment.ExplorerEvidencePath ?? string.Empty).Equals(Path.GetFullPath(explorer), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Explorer evidence path does not match the validator input.");
        if (string.IsNullOrWhiteSpace(environment.AgentExecutablePath) || !File.Exists(environment.AgentExecutablePath)) throw new FileNotFoundException("The live Agent executable is missing.", environment.AgentExecutablePath);
        if (string.IsNullOrWhiteSpace(environment.WorkloadExecutablePath) || !File.Exists(environment.WorkloadExecutablePath)) throw new FileNotFoundException("The live workload executable is missing.", environment.WorkloadExecutablePath);
        if (string.IsNullOrWhiteSpace(environment.WorkloadOraclePath) || !File.Exists(environment.WorkloadOraclePath)) throw new FileNotFoundException("The live workload oracle is missing.", environment.WorkloadOraclePath);
        if (string.IsNullOrWhiteSpace(environment.ExplorerEvidencePath) || !File.Exists(environment.ExplorerEvidencePath)) throw new FileNotFoundException("The live Explorer evidence is missing.", environment.ExplorerEvidencePath);
    }

    private static void Write(string path, object value)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full) ?? throw new InvalidOperationException("The output path has no parent directory.");
        Directory.CreateDirectory(parent);
        File.WriteAllText(full, JsonSerializer.Serialize(value, JsonOptions));
    }

    private static bool TryParse(string[] args, out Options options, out string error)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) { options = null!; error = "Arguments must be --name value pairs."; return false; }
            values[args[i][2..]] = args[++i];
        }
        if (!values.TryGetValue("oracle", out var oracle) || !values.TryGetValue("history", out var history) || !values.TryGetValue("explorer", out var explorer) || !values.TryGetValue("environment", out var environment) || !values.TryGetValue("output", out var output)) { options = null!; error = "oracle, history, explorer, environment, and output are required."; return false; }
        options = new Options(oracle, history, explorer, environment, output);
        error = string.Empty;
        return true;
    }

    private sealed record Options(string OraclePath, string HistoryPath, string ExplorerPath, string EnvironmentPath, string OutputPath);
    private sealed record OracleDocument(string Schema, string RunId, string Scenario, DateTimeOffset CompletedUtc, ProcessEvidence Process, IReadOnlyList<ProcessEvidence>? Processes, int RecordCount, IReadOnlyList<OracleOperation> Operations);
    private sealed record ProcessEvidence(int ProcessId, DateTime StartTimeUtc, string ExecutablePath, string ScenarioId);
    private sealed record OracleOperation(long Sequence, string Operation, string RelativePath, string? OldRelativePath, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc);
    private sealed record ExplorerDocument(string Schema, IReadOnlyList<ExplorerScenario> Rows);
    private sealed record ExplorerScenario(string ScenarioId, string Operation, string DestinationRelativePath, string? SourceRelativePath, string? ExpectedCorrelation, string? ExpectedSourceFileId);
    private sealed record CorrelationEnvironment(string? TargetOs, string? VmName, string? ExecutionMode, string? AgentHostMode, bool Diagnostic, string? AgentHistoryPath, string? AgentExecutablePath, string? WorkloadExecutablePath, string? WorkloadOraclePath, string? ExplorerEvidencePath);
    private sealed record ProcessMeasurement(int Total, int Exact, int Correlated, int Unknown, int FalseExactCount, decimal ExactRate, decimal CorrelatedRate, decimal UnknownRate, IReadOnlyList<object> Rows);
    private sealed record ExplorerMeasurement(int CopyIntentCount, int SourceCorrelatedCount, int SourceUnknownCount, int NotIdentifiedCount, int FalseAttributionCount, IReadOnlyList<object> Rows);
    private sealed record FileStateCorrectness(int ExpectedCount, int VerifiedCount, int MissingCount, int DroppedEventCount, int DistinctPathCount);
}
