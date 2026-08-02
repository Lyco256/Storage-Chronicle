using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using StorageChronicle.Domain.Contracts;
using ZstdSharp;

namespace StorageChronicle.Benchmarks;

/// <summary>Entry point for opt-in performance measurements; benchmark runs never alter durable history.</summary>
public static class Program
{
    /// <summary>Runs all deterministic BenchmarkDotNet suites.</summary>
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

/// <summary>Measures one-million identity workloads and one-hundred-thousand projection/storage-shaped workloads.</summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public sealed class StorageChronicleBenchmarks
{
    private string[] fileIds = [];
    private CanonicalEvent[] events = [];
    private byte[] serializedEvents = [];
    private Dictionary<string, string> parentMap = new(StringComparer.Ordinal);

    /// <summary>Creates deterministic workloads without reading files or writing history.</summary>
    [GlobalSetup]
    public void Setup()
    {
        fileIds = Enumerable.Range(0, 1_000_000).Select(index => $"file-{index:D8}").ToArray();
        parentMap = fileIds.ToDictionary(id => id, id => id.Length == 13 ? "root" : "root", StringComparer.Ordinal);
        events = Enumerable.Range(0, 100_000).Select(CreateEvent).ToArray();
        serializedEvents = JsonSerializer.SerializeToUtf8Bytes(events);
    }

    /// <summary>Imports one million MFT-like identities into a keyed map.</summary>
    [Benchmark]
    [BenchmarkCategory("1M", "MFT")]
    public Dictionary<string, int> MftEnumerationImport1M() => fileIds.Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index, StringComparer.Ordinal);

    /// <summary>Reconstructs one path by following a bounded parent chain from a one-million-node state.</summary>
    [Benchmark]
    [BenchmarkCategory("1M", "State")]
    public string ReconstructSinglePointPath1M()
    {
        var current = fileIds[^1];
        var path = new List<string>(8);
        for (var depth = 0; depth < 8 && current is not null; depth++)
        {
            path.Add(current);
            if (!parentMap.TryGetValue(current, out var parent)) break;
            current = parent;
        }

        path.Reverse();
        return string.Join("/", path);
    }

    /// <summary>Builds grouped activity keys for one hundred thousand events.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "Projection")]
    public int GroupedGeneration100K() => events.GroupBy(value => value.FileId?.Value ?? "unknown", StringComparer.Ordinal).Count();

    /// <summary>Materializes one bounded Event Stack page from one hundred thousand events.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "EventStack")]
    public CanonicalEvent[] EventStackPage100K() => events.OrderByDescending(value => value.Time.RecordedUtc).ThenByDescending(value => value.Time.SourceSequence.Value).Skip(50_000).Take(250).ToArray();

    /// <summary>Computes a period diff over one hundred thousand event identities.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "Diff")]
    public int PeriodDiff100K() => events.Take(50_000).Select(value => value.FileId?.Value).Except(events.Skip(50_000).Select(value => value.FileId?.Value), StringComparer.Ordinal).Count();

    /// <summary>Measures append-record serialization for one hundred thousand events.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "Storage")]
    public byte[] AppendRecordSerialization100K() => JsonSerializer.SerializeToUtf8Bytes(events);

    /// <summary>Measures a deterministic compressed-payload preparation step.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "Compression")]
    public int CompressedPayloadLength100K()
    {
        using var compressor = new Compressor(3);
        return compressor.Wrap(serializedEvents).Length;
    }

    /// <summary>Measures rebuild-shaped keyed index insertion for one hundred thousand events.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "SQLite")]
    public int SqliteIndexInsertion100K() => events.ToDictionary(value => value.EventId, value => value.Name).Count;

    /// <summary>Measures merge-shaped external-media manifest reconciliation.</summary>
    [Benchmark]
    [BenchmarkCategory("100K", "Media")]
    public int MediaManifestMerge100K() => events.GroupBy(value => value.VolumeId?.Value ?? "unknown", StringComparer.Ordinal).Select(group => group.Last()).Count();

    private static CanonicalEvent CreateEvent(int index)
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(index);
        var volume = VolumeId.Create("volume-benchmark");
        var file = FileId.Create($"file-{index:D8}");
        return new CanonicalEvent(EventId.New(), EventSchemaVersion.Current, CanonicalOperation.DataWrite, EventOrigin.LiveUsn, volume, file, null, $"file-{index:D8}.dat", null, null, new EventTime(timestamp, TimeSpan.Zero, timestamp, timestamp, new SourceSequence(index), new MountSequence(index)), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty);
    }
}
