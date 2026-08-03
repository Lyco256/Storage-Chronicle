# Benchmark.sh

## Role

Runs the real BenchmarkDotNet project on a Unix-like host.

## Inputs and outputs

BenchmarkDotNet arguments are forwarded to the benchmark executable; results are written under its configured artifact directory.

## Failure behavior

BenchmarkDotNet failures propagate and no result is treated as acceptance evidence unless the requested workload actually ran.

## Tests

The benchmark project covers projection/state, append/segment/SQLite, media manifests, and Windows-only MFT workloads where available.
