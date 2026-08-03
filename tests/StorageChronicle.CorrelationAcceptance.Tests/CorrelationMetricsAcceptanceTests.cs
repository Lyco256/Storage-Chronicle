using System.Collections.Immutable;
using System.Text.Json;
using StorageChronicle.Application;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Normalization;
using StorageChronicle.Platform.Windows.Session;
using StorageChronicle.Projection;
using StorageChronicle.SessionAgent;
using Xunit;
using FileIdentifier = StorageChronicle.Domain.Contracts.FileId;
using MountIdentifier = StorageChronicle.Domain.Contracts.MountSessionId;
using ProcessIdentifier = StorageChronicle.Domain.Contracts.ProcessInstanceId;
using VolumeIdentifier = StorageChronicle.Domain.Contracts.VolumeId;

namespace StorageChronicle.CorrelationAcceptance.Tests;

/// <summary>Runs the deterministic R-00 process and Explorer-correlation acceptance fixture.</summary>
public sealed class CorrelationMetricsAcceptanceTests
{
    private const string FixtureFileName = "r00-process-explorer-correlation.json";
    private const string FixtureId = "r00-process-explorer-correlation-v1";
    private static readonly DateTimeOffset FixtureEpoch = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions FixtureJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions ReportJsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Measures attribution quality and Explorer source correlation through Agent and Projection.</summary>
    [Fact]
    [Trait("Category", "Acceptance")]
    [Trait("Requirement", "R-00")]
    public async Task FixedFixtureProducesStableAttributionAndExplorerMetrics()
    {
        var fixturePath = Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_CORRELATION_FIXTURE")
            ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureFileName);
        var fixture = CorrelationFixture.Load(fixturePath);
        Assert.Equal(FixtureId, fixture.FixtureId);
        Assert.Equal(3, fixture.ProcessCases.Count);
        Assert.Equal(4, fixture.ExplorerCases.Count);

        var sourceDefinitions = fixture.SourceFacts.ToArray();
        var sourceFacts = sourceDefinitions.Select(value => value.ToSource(FixtureEpoch)).ToArray();
        var sourceById = sourceDefinitions.ToDictionary(value => value.SourceId, value => value.ToSource(FixtureEpoch), StringComparer.Ordinal);
        Assert.Equal(sourceFacts.Length, sourceById.Count);
        foreach (var processCase in fixture.ProcessCases)
        {
            Assert.Contains(processCase.SourceId, sourceById.Keys);
        }

        var eventStore = new RecordingEventStore();
        var stateStore = new RecordingStateStore();
        var pipeline = new AgentPipeline(eventStore, stateStore, new EventNormalizer());
        await pipeline.RunAsync(new FixtureCollector(sourceFacts), TestContext.Current.CancellationToken);

        // Source facts remain a separate input set. Clipboard and ETW-read observations
        // are correlation-only and must not become durable file-history rows.
        Assert.Equal(11, sourceFacts.Length);
        Assert.Equal(7, eventStore.Sources.Count);
        Assert.Equal(7, eventStore.CanonicalEvents.Count);
        Assert.Equal(eventStore.CanonicalEvents.Count, stateStore.Applied.Count);
        Assert.DoesNotContain(eventStore.Sources, value => value.Origin == EventOrigin.Clipboard);
        Assert.DoesNotContain(eventStore.CanonicalEvents, value => value.Origin == EventOrigin.Clipboard);
        Assert.DoesNotContain(eventStore.CanonicalEvents, value => value.Origin == EventOrigin.Etw);
        Assert.Equal(eventStore.Sources.Select(value => value.EventId), eventStore.CanonicalEvents.Select(value => value.EventId));

        var canonicalBySourceId = eventStore.CanonicalEvents.ToDictionary(value => FindSourceId(value.EventId, sourceDefinitions), StringComparer.Ordinal);
        var processQuality = fixture.ProcessCases
            .Select(value => (Case: value, Event: canonicalBySourceId[value.SourceId]))
            .ToArray();
        foreach (var item in processQuality)
        {
            Assert.Equal(item.Case.ExpectedQuality, item.Event.ProcessQuality.ToString());
        }

        var processMeasurement = new ProcessMeasurement(
            processQuality.Length,
            processQuality.Count(value => value.Event.ProcessQuality == ProcessAttributionQuality.Exact),
            processQuality.Count(value => value.Event.ProcessQuality == ProcessAttributionQuality.Correlated),
            processQuality.Count(value => value.Event.ProcessQuality == ProcessAttributionQuality.Unknown));
        Assert.Equal(1, processMeasurement.Exact);
        Assert.Equal(1, processMeasurement.Correlated);
        Assert.Equal(1, processMeasurement.Unknown);

        var explorerMeasurement = MeasureExplorerCorrelation(fixture.ExplorerCases, canonicalBySourceId);
        Assert.Equal(3, explorerMeasurement.ExplorerCandidates);
        Assert.Equal(1, explorerMeasurement.Correlated);
        Assert.Equal(2, explorerMeasurement.Uncorrelated);
        Assert.Equal(1, explorerMeasurement.ExcludedNonExplorer);
        Assert.Equal(0.3333m, explorerMeasurement.CorrelationRate);

        var sessionClipboardSource = sourceById["clipboard-copy-correlated"];
        var sessionMessage = ClipboardCandidateMessage.FromSourceEvent(sessionClipboardSource);
        Assert.Equal(1, sessionMessage.Generation);
        Assert.False(sessionMessage.IsCut);
        Assert.Single(sessionMessage.Paths);
        Assert.Equal("ConfirmedIntent", sessionMessage.Quality);
        Assert.Equal(sessionMessage.Generation, sessionMessage.ToAgentRequest().Generation);

        var projection = await new ProjectionService(new ProjectionDocument(eventStore.CanonicalEvents, eventStore.Sources))
            .GetEventStackTreeAsync(new EventStackQuery(EventStackMode.Grouped, 1, 50), TestContext.Current.CancellationToken);
        var roots = projection.Items;
        var exactRoot = FindRoot(roots, sourceById["process-exact"].EventId);
        var correlatedRoot = FindRoot(roots, sourceById["process-correlated"].EventId);
        var unknownRoot = FindRoot(roots, sourceById["process-unknown"].EventId);
        var explorerRoot = FindRoot(roots, sourceById["explorer-copy-correlated"].EventId);

        Assert.Equal("editor.exe", exactRoot.ProcessDisplayName);
        Assert.Equal(ProcessAttributionQuality.Exact, exactRoot.Row.ProcessQuality);
        Assert.Equal("process-parent-instance", exactRoot.Process?.ParentProcessInstanceId?.Value);
        Assert.Equal("worker.exe", correlatedRoot.ProcessDisplayName);
        Assert.Equal(ProcessAttributionQuality.Correlated, correlatedRoot.Row.ProcessQuality);
        Assert.Equal("不明なプロセス", unknownRoot.ProcessDisplayName);
        Assert.Equal(ProcessAttributionQuality.Unknown, unknownRoot.Row.ProcessQuality);
        Assert.Equal("Explorer操作", explorerRoot.ProcessDisplayName);
        Assert.Equal(ProcessAttributionQuality.Exact, explorerRoot.Row.ProcessQuality);

        var report = new CorrelationMeasurementReport(
            "PASSED_FIXTURE",
            fixture.FixtureId,
            sourceFacts.Length,
            eventStore.Sources.Count,
            eventStore.CanonicalEvents.Count,
            sourceFacts.Length - eventStore.Sources.Count,
            processMeasurement,
            explorerMeasurement,
            new ProjectionMeasurement(exactRoot.ProcessDisplayName, correlatedRoot.ProcessDisplayName, unknownRoot.ProcessDisplayName, explorerRoot.ProcessDisplayName));
        WriteReportIfRequested(report);
        Console.WriteLine($"CORRELATION_METRICS fixture={report.FixtureId} process=Exact:{processMeasurement.Exact}/{processMeasurement.Total},Correlated:{processMeasurement.Correlated}/{processMeasurement.Total},Unknown:{processMeasurement.Unknown}/{processMeasurement.Total} explorer={explorerMeasurement.Correlated}/{explorerMeasurement.ExplorerCandidates} rate={explorerMeasurement.CorrelationRate:F4}");
    }

    private static ExplorerMeasurement MeasureExplorerCorrelation(IReadOnlyList<ExplorerCaseDefinition> cases, IReadOnlyDictionary<string, CanonicalEvent> canonicalBySourceId)
    {
        var candidates = 0;
        var correlated = 0;
        var uncorrelated = 0;
        var excludedNonExplorer = 0;
        foreach (var item in cases)
        {
            var value = canonicalBySourceId[item.DestinationSourceId];
            var processName = value.Properties.GetValueOrDefault("process.name");
            var isExplorer = processName?.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase) == true;
            var hasCorrelation = string.Equals(value.Properties.GetValueOrDefault("copycorrelationquality"), EventQuality.Correlated.ToString(), StringComparison.Ordinal);
            if (!isExplorer)
            {
                excludedNonExplorer++;
                Assert.Equal("ExcludedNonExplorer", item.ExpectedCorrelation);
                Assert.False(hasCorrelation);
                Assert.DoesNotContain("copysourcefileid", value.Properties.Keys);
                continue;
            }

            candidates++;
            if (hasCorrelation)
            {
                correlated++;
                Assert.Equal("Correlated", item.ExpectedCorrelation);
                Assert.Equal(item.ExpectedSourceFileId, value.Properties.GetValueOrDefault("copysourcefileid"));
            }
            else
            {
                uncorrelated++;
                Assert.Equal("Uncorrelated", item.ExpectedCorrelation);
                Assert.Null(item.ExpectedSourceFileId);
                Assert.DoesNotContain("copysourcefileid", value.Properties.Keys);
            }
        }

        return new ExplorerMeasurement(candidates, correlated, uncorrelated, excludedNonExplorer);
    }

    private static EventStackNode FindRoot(IReadOnlyList<EventStackNode> roots, EventId eventId) =>
        roots.FirstOrDefault(value => value.Row.Id == eventId) ??
        Flatten(roots).First(value => value.Row.Id == eventId);

    private static IEnumerable<EventStackNode> Flatten(IEnumerable<EventStackNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    private static string FindSourceId(EventId eventId, IReadOnlyList<SourceFactDefinition> facts) =>
        facts.First(value => new EventId(Guid.Parse(value.EventId)) == eventId).SourceId;

    private static void WriteReportIfRequested(CorrelationMeasurementReport report)
    {
        var path = Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_CORRELATION_REPORT");
        if (string.IsNullOrWhiteSpace(path)) return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("The correlation report path has no parent directory.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(report, ReportJsonOptions));
    }

    private sealed class FixtureCollector(IReadOnlyList<SourceEvent> values) : ISourceEventCollector
    {
        public async IAsyncEnumerable<SourceEvent> CollectAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return value;
                await Task.Yield();
            }
        }
    }

    private sealed class RecordingEventStore : IEventStore
    {
        public List<SourceEvent> Sources { get; } = [];
        public List<CanonicalEvent> CanonicalEvents { get; } = [];
        public ValueTask AppendSourceAsync(SourceEvent value, CancellationToken cancellationToken = default) { Sources.Add(value); return ValueTask.CompletedTask; }
        public ValueTask AppendCanonicalAsync(CanonicalEvent value, CancellationToken cancellationToken = default) { CanonicalEvents.Add(value); return ValueTask.CompletedTask; }
        public async IAsyncEnumerable<CanonicalEvent> ReadCanonicalAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var value in CanonicalEvents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return value;
                await Task.Yield();
            }
        }
    }

    private sealed class RecordingStateStore : IStateStore
    {
        public List<CanonicalEvent> Applied { get; } = [];
        public ValueTask ApplyAsync(CanonicalEvent value, CancellationToken cancellationToken = default) { Applied.Add(value); return ValueTask.CompletedTask; }
        public ValueTask<FileStateSnapshot> GetSnapshotAsync(DateTimeOffset atUtc, CancellationToken cancellationToken = default) => ValueTask.FromResult(new FileStateSnapshot(atUtc, [], []));
    }

    private sealed record CorrelationFixture(string FixtureId, string Description, IReadOnlyList<ProcessCaseDefinition> ProcessCases, IReadOnlyList<ExplorerCaseDefinition> ExplorerCases, IReadOnlyList<SourceFactDefinition> SourceFacts)
    {
        public static CorrelationFixture Load(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The R-00 correlation fixture is missing; acceptance was not executed.", path);
            var json = File.ReadAllText(path);
            RejectSensitivePropertyNames(json, path);
            var value = JsonSerializer.Deserialize<CorrelationFixture>(json, FixtureJsonOptions);
            return value ?? throw new InvalidDataException("The R-00 correlation fixture is empty.");
        }

        private static void RejectSensitivePropertyNames(string json, string path)
        {
            using var document = JsonDocument.Parse(json);
            foreach (var property in EnumerateProperties(document.RootElement))
            {
                var normalized = property.Name.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
                if (normalized.Contains("content", StringComparison.Ordinal) || normalized.Contains("hash", StringComparison.Ordinal) || normalized.Contains("mime", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"The correlation fixture contains a forbidden content/hash property: {property.Name} ({path}).");
                }
            }
        }

        private static IEnumerable<JsonProperty> EnumerateProperties(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    yield return property;
                    foreach (var nested in EnumerateProperties(property.Value)) yield return nested;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                {
                    foreach (var nested in EnumerateProperties(child)) yield return nested;
                }
            }
        }
    }

    private sealed record ProcessCaseDefinition(string SourceId, string ExpectedQuality);
    private sealed record ExplorerCaseDefinition(string DestinationSourceId, string? ClipboardSourceId, string ExpectedCorrelation, string? ExpectedSourceFileId);
    private sealed record SourceFactDefinition(
        string SourceId,
        string EventId,
        string Origin,
        string? VolumeId,
        string? FileId,
        string? ParentFileId,
        string? Name,
        string? Hint,
        string Quality,
        string? ProcessInstanceId,
        string ProcessQuality,
        string? MountSessionId,
        long Sequence,
        IReadOnlyDictionary<string, string>? Properties)
    {
        public SourceEvent ToSource(DateTimeOffset epoch)
        {
            if (!Enum.TryParse<EventOrigin>(Origin, true, out var parsedOrigin) ||
                !Enum.TryParse<EventQuality>(Quality, true, out var parsedQuality) ||
                !Enum.TryParse<ProcessAttributionQuality>(ProcessQuality, true, out var parsedProcessQuality))
            {
                throw new InvalidDataException($"Invalid enum in source fixture '{SourceId}'.");
            }

            CanonicalOperation? operation = null;
            if (!string.IsNullOrWhiteSpace(Hint))
            {
                if (!Enum.TryParse(Hint, true, out CanonicalOperation parsedOperation))
                {
                    throw new InvalidDataException($"Invalid operation in source fixture '{SourceId}'.");
                }

                operation = parsedOperation;
            }

            var recorded = epoch.AddSeconds(Sequence);
            var properties = (Properties ?? new Dictionary<string, string>(StringComparer.Ordinal)).ToImmutableDictionary(StringComparer.Ordinal);
            return new SourceEvent(
                new EventId(Guid.Parse(EventId)),
                EventSchemaVersion.Current,
                parsedOrigin,
                ParseId<VolumeIdentifier>(VolumeId, VolumeIdentifier.Create),
                ParseId<FileIdentifier>(FileId, FileIdentifier.Create),
                ParseId<FileIdentifier>(ParentFileId, FileIdentifier.Create),
                Name,
                null,
                operation,
                null,
                new EventTime(recorded, TimeSpan.Zero, recorded, recorded, new SourceSequence(Sequence), new MountSequence(Sequence)),
                parsedQuality,
                ParseId<ProcessIdentifier>(ProcessInstanceId, ProcessIdentifier.Create),
                parsedProcessQuality,
                ParseId<MountIdentifier>(MountSessionId, MountIdentifier.Create),
                null,
                properties);
        }

        private static T? ParseId<T>(string? value, Func<string, T> parser) where T : struct => string.IsNullOrWhiteSpace(value) ? null : parser(value);
    }

    private sealed record ProcessMeasurement(int Total, int Exact, int Correlated, int Unknown)
    {
        public decimal ExactRate => Rate(Exact, Total);
        public decimal CorrelatedRate => Rate(Correlated, Total);
        public decimal UnknownRate => Rate(Unknown, Total);
    }

    private sealed record ExplorerMeasurement(int ExplorerCandidates, int Correlated, int Uncorrelated, int ExcludedNonExplorer)
    {
        public decimal CorrelationRate => Rate(Correlated, ExplorerCandidates);
    }

    private sealed record ProjectionMeasurement(string ExactDisplay, string CorrelatedDisplay, string UnknownDisplay, string ExplorerDisplay);
    private sealed record CorrelationMeasurementReport(string Status, string FixtureId, int SourceFactCount, int DurableSourceFactCount, int CanonicalEventCount, int TransientCorrelationSourceFactCount, ProcessMeasurement ProcessAttribution, ExplorerMeasurement ExplorerSourceCorrelation, ProjectionMeasurement Projection);

    private static decimal Rate(int numerator, int denominator) => denominator == 0 ? 0m : decimal.Round((decimal)numerator / denominator, 4, MidpointRounding.AwayFromZero);
}

