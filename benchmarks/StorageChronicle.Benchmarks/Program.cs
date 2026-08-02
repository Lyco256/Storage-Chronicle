using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace StorageChronicle.Benchmarks;

/// <summary>Entry point for opt-in performance measurements; benchmark runs never alter history.</summary>
public static class Program
{
    /// <summary>Runs the selected BenchmarkDotNet suite.</summary>
    public static void Main(string[] args) => BenchmarkRunner.Run<ProjectionBenchmarks>();
}

/// <summary>Small deterministic projection benchmark used to detect algorithmic regressions.</summary>
[MemoryDiagnoser]
public class ProjectionBenchmarks
{
    private readonly string[] routes = Enumerable.Range(0, 1000).Select(index => $"/root/file-{index}.dat").ToArray();

    /// <summary>Measures route sorting work without accessing the filesystem.</summary>
    [Benchmark]
    public string[] SortRoutes() => routes.OrderBy(value => value, StringComparer.Ordinal).ToArray();
}
