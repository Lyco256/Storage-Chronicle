# Test-Performance.ps1

Runs the caller-selected BenchmarkDotNet filter and stores JSON/Markdown reports under `artifacts/benchmarks`. Its default filter selects only the `StorageChronicleBenchmarks` type; it is therefore a focused diagnostic entry point, not proof that the R-03/R-19 full matrix ran. Use `build/quality/Test-FullBenchmarkMatrix.ps1 -IncludeMft` for the fail-closed matrix that validates every required suite and method. Benchmark output is diagnostic evidence, not an acceptance claim for release hardware.
