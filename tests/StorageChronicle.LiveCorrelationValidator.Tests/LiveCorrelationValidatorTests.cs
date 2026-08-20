using System.Collections.Immutable;
using System.Text.Json;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.LiveCorrelationValidator;
using StorageChronicle.Storage;
using Xunit;

namespace StorageChronicle.LiveCorrelationValidator.Tests;

public sealed class LiveCorrelationValidatorTests
{
    [Fact]
    public async Task RealShapedCorrelationEvidencePassesOnlyWithMatchingRowsAndArtifacts()
    {
        var root = CreateTempRoot();
        try
        {
            var processId = 4321;
            var processStart = DateTime.UtcNow.AddMinutes(-1);
            var historyPath = Path.Combine(root, "history");
            var oraclePath = Path.Combine(root, "oracle.json");
            var explorerPath = Path.Combine(root, "explorer.json");
            var environmentPath = Path.Combine(root, "environment.json");
            var outputPath = Path.Combine(root, "evidence.json");
            var agentPath = Path.Combine(root, "StorageChronicle.Agent.exe");
            var workloadPath = Path.Combine(root, "StorageChronicle.FileMutationWorkload.exe");
            await File.WriteAllTextAsync(agentPath, "test artifact");
            await File.WriteAllTextAsync(workloadPath, "test artifact");

            await CreateHistoryAsync(historyPath, processId, processStart);
            WriteOracle(oraclePath, processId, processStart);
            WriteExplorer(explorerPath);
            WriteEnvironment(environmentPath, historyPath, oraclePath, explorerPath, agentPath, workloadPath);

            var exitCode = await Program.Main(new[]
            {
                "--oracle", oraclePath, "--history", historyPath, "--explorer", explorerPath,
                "--environment", environmentPath, "--output", outputPath
            });

            Assert.Equal(0, exitCode);
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal("PASSED", evidence.RootElement.GetProperty("Status").GetString());
            Assert.True(evidence.RootElement.GetProperty("AcceptanceEligible").GetBoolean());
            Assert.Equal(0, evidence.RootElement.GetProperty("FalseExactCount").GetInt32());
            Assert.Equal(0, evidence.RootElement.GetProperty("ExplorerSourceCorrelation").GetProperty("FalseAttributionCount").GetInt32());
            Assert.Equal(1, evidence.RootElement.GetProperty("FileStateCorrectness").GetProperty("VerifiedCount").GetInt32());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task UnknownExactProcessAttributionFailsClosed()
    {
        var root = CreateTempRoot();
        try
        {
            var processStart = DateTime.UtcNow.AddMinutes(-1);
            var historyPath = Path.Combine(root, "history");
            var oraclePath = Path.Combine(root, "oracle.json");
            var explorerPath = Path.Combine(root, "explorer.json");
            var environmentPath = Path.Combine(root, "environment.json");
            var outputPath = Path.Combine(root, "evidence.json");
            var agentPath = Path.Combine(root, "StorageChronicle.Agent.exe");
            var workloadPath = Path.Combine(root, "StorageChronicle.FileMutationWorkload.exe");
            await File.WriteAllTextAsync(agentPath, "test artifact");
            await File.WriteAllTextAsync(workloadPath, "test artifact");

            await CreateHistoryAsync(historyPath, 9999, processStart);
            WriteOracle(oraclePath, 4321, processStart);
            WriteExplorer(explorerPath);
            WriteEnvironment(environmentPath, historyPath, oraclePath, explorerPath, agentPath, workloadPath);

            var exitCode = await Program.Main(new[]
            {
                "--oracle", oraclePath, "--history", historyPath, "--explorer", explorerPath,
                "--environment", environmentPath, "--output", outputPath
            });

            Assert.Equal(2, exitCode);
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal("FAILED", evidence.RootElement.GetProperty("Status").GetString());
            Assert.Equal(1, evidence.RootElement.GetProperty("FalseExactCount").GetInt32());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task DiagnosticEnvironmentCannotBecomeEligible()
    {
        var root = CreateTempRoot();
        try
        {
            var historyPath = Path.Combine(root, "history");
            var oraclePath = Path.Combine(root, "oracle.json");
            var explorerPath = Path.Combine(root, "explorer.json");
            var environmentPath = Path.Combine(root, "environment.json");
            var outputPath = Path.Combine(root, "evidence.json");
            var agentPath = Path.Combine(root, "StorageChronicle.Agent.exe");
            var workloadPath = Path.Combine(root, "StorageChronicle.FileMutationWorkload.exe");
            Directory.CreateDirectory(historyPath);
            await File.WriteAllTextAsync(oraclePath, "{}");
            await File.WriteAllTextAsync(explorerPath, "{}");
            await File.WriteAllTextAsync(agentPath, "test artifact");
            await File.WriteAllTextAsync(workloadPath, "test artifact");
            WriteEnvironment(environmentPath, historyPath, oraclePath, explorerPath, agentPath, workloadPath, diagnostic: true);

            var exitCode = await Program.Main(new[]
            {
                "--oracle", oraclePath, "--history", historyPath, "--explorer", explorerPath,
                "--environment", environmentPath, "--output", outputPath
            });

            Assert.Equal(2, exitCode);
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            Assert.False(evidence.RootElement.GetProperty("AcceptanceEligible").GetBoolean());
            Assert.Equal("FAILED", evidence.RootElement.GetProperty("Status").GetString());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task MissingFinalStateCannotBecomeEligibleEvenWhenCanonicalEvidenceExists()
    {
        var root = CreateTempRoot();
        try
        {
            var processStart = DateTime.UtcNow.AddMinutes(-1);
            var historyPath = Path.Combine(root, "history");
            var oraclePath = Path.Combine(root, "oracle.json");
            var explorerPath = Path.Combine(root, "explorer.json");
            var environmentPath = Path.Combine(root, "environment.json");
            var outputPath = Path.Combine(root, "evidence.json");
            var agentPath = Path.Combine(root, "StorageChronicle.Agent.exe");
            var workloadPath = Path.Combine(root, "StorageChronicle.FileMutationWorkload.exe");
            await File.WriteAllTextAsync(agentPath, "test artifact");
            await File.WriteAllTextAsync(workloadPath, "test artifact");

            await CreateHistoryAsync(historyPath, 4321, processStart, applyFileState: false);
            WriteOracle(oraclePath, 4321, processStart);
            WriteExplorer(explorerPath);
            WriteEnvironment(environmentPath, historyPath, oraclePath, explorerPath, agentPath, workloadPath);

            var exitCode = await Program.Main(new[]
            {
                "--oracle", oraclePath, "--history", historyPath, "--explorer", explorerPath,
                "--environment", environmentPath, "--output", outputPath
            });

            Assert.Equal(2, exitCode);
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal("FAILED", evidence.RootElement.GetProperty("Status").GetString());
            Assert.Equal(1, evidence.RootElement.GetProperty("FileStateCorrectness").GetProperty("MissingCount").GetInt32());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static async Task CreateHistoryAsync(string historyPath, int processId, DateTime processStart, bool applyFileState = true)
    {
        var volume = VolumeId.Create("test-volume");
        var parent = FileId.Create("parent");
        var file = FileId.Create("copy.txt");
        var now = DateTimeOffset.UtcNow;
        var parentMetadata = Metadata(volume, parent, null, "folder", FileKind.Directory, true);
        var fileMetadata = Metadata(volume, file, parent, "copy.txt", FileKind.File, true);
        var process = ProcessInstanceId.Create($"{processId}:{processStart.Ticks}");
        var properties = ImmutableDictionary<string, string>.Empty
            .Add("process.executable", "C:\\Windows\\explorer.exe")
            .Add("copyCorrelationQuality", "Correlated")
            .Add("copySourceFileId", "source-file");
        await using var history = new AppendOnlyStorageEngine(new StorageEngineOptions(historyPath) { FlushInterval = TimeSpan.FromMinutes(10) });
        var parentSource = Source(volume, parent, null, "folder", CanonicalOperation.DirectoryCreate, parentMetadata, now, 1, null, ProcessAttributionQuality.Unknown, ImmutableDictionary<string, string>.Empty);
        var parentCanonical = Canonical(parentSource, CanonicalOperation.DirectoryCreate, parentMetadata);
        await history.AppendSourceAsync(parentSource);
        await history.AppendCanonicalAsync(parentCanonical);
        await history.ApplyAsync(parentCanonical);

        var fileSource = Source(volume, file, parent, "copy.txt", CanonicalOperation.Create, fileMetadata, now.AddMilliseconds(1), 2, process, ProcessAttributionQuality.Exact, properties);
        var fileCanonical = Canonical(fileSource, CanonicalOperation.Create, fileMetadata);
        await history.AppendSourceAsync(fileSource);
        await history.AppendCanonicalAsync(fileCanonical);
        if (applyFileState) await history.ApplyAsync(fileCanonical);
    }

    private static void WriteOracle(string path, int processId, DateTime processStart)
    {
        var operation = new
        {
            Sequence = 1L,
            Operation = "Create",
            RelativePath = "folder\\copy.txt",
            OldRelativePath = (string?)null,
            StartedUtc = DateTimeOffset.UtcNow,
            CompletedUtc = DateTimeOffset.UtcNow
        };
        var process = new { ProcessId = processId, StartTimeUtc = processStart, ExecutablePath = "StorageChronicle.FileMutationWorkload.exe", ScenarioId = "full" };
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Schema = "StorageChronicle.FileMutationWorkload.v2",
            RunId = "test-run",
            Scenario = "full",
            CompletedUtc = DateTimeOffset.UtcNow,
            Process = process,
            Processes = new[] { process },
            RecordCount = 1,
            Operations = new[] { operation }
        }));
    }

    private static void WriteExplorer(string path) => File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        Schema = "StorageChronicle.ExplorerScenario.v1",
        Rows = new[] { new { ScenarioId = "copy-1", Operation = "Copy", DestinationRelativePath = "folder\\copy.txt", SourceRelativePath = "source.txt", ExpectedCorrelation = "Correlated", ExpectedSourceFileId = "source-file" } }
    }));

    private static void WriteEnvironment(string path, string history, string oracle, string explorer, string agent, string workload, bool diagnostic = false) => File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        TargetOs = "Windows11",
        VmName = "SC-Test-W11-VBox",
        ExecutionMode = "TestLab",
        AgentHostMode = "TestLab",
        Diagnostic = diagnostic,
        AgentHistoryPath = history,
        AgentExecutablePath = agent,
        WorkloadExecutablePath = workload,
        WorkloadOraclePath = oracle,
        ExplorerEvidencePath = explorer
    }));

    private static SourceEvent Source(VolumeId volume, FileId file, FileId? parent, string name, CanonicalOperation operation, FileMetadata metadata, DateTimeOffset time, long sequence, ProcessInstanceId? process, ProcessAttributionQuality processQuality, ImmutableDictionary<string, string> properties) =>
        new(EventId.New(), EventSchemaVersion.Current, EventOrigin.LiveUsn, volume, file, parent, name, null, operation, metadata, Time(time, sequence), EventQuality.Exact, process, processQuality, null, null, properties);

    private static CanonicalEvent Canonical(SourceEvent source, CanonicalOperation operation, FileMetadata metadata) =>
        new(source.EventId, source.SchemaVersion, operation, source.Origin, source.VolumeId, source.FileId, source.ParentFileId, source.Name, source.OldName, metadata, source.Time, source.Quality, source.ProcessInstanceId, source.ProcessQuality, source.MountSessionId, source.OperationCorrelationId, source.Properties);

    private static FileMetadata Metadata(VolumeId volume, FileId file, FileId? parent, string name, FileKind kind, bool exists) =>
        new(volume, file, parent, name, kind, null, null, null, null, null, null, FileAttributes.Normal, null, null, EventQuality.Exact, exists, false);

    private static EventTime Time(DateTimeOffset now, long sequence) => new(now, TimeSpan.Zero, null, now, new SourceSequence(sequence), new MountSequence(sequence));

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "StorageChronicle.LiveCorrelationValidator", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempRoot(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
