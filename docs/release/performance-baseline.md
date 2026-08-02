# Performance baseline

The repository contains an opt-in BenchmarkDotNet harness at `benchmarks/StorageChronicle.Benchmarks` and deterministic load generation at `tools/StorageChronicle.TestDataGenerator`. CI records benchmark output under `artifacts/` only; generated output is not committed.

The 50 MiB idle working-set and 0.5% average CPU thresholds are acceptance gates for a release hardware run, not values inferred from a developer workstation. A release is not performance-approved until the run records private working set, CPU average, queue depth, disk writes, and the exact OS/build/configuration.
