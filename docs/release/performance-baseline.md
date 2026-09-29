# Performance baseline

The repository contains an opt-in BenchmarkDotNet harness at `benchmarks/StorageChronicle.Benchmarks` and deterministic load generation at `tools/StorageChronicle.TestDataGenerator`. CI records benchmark output under `artifacts/` only; generated output is not committed.

The 50 MiB private-working-set and 0.5% average CPU thresholds are acceptance gates for a release hardware run, not values inferred from a developer workstation. A release is not performance-approved until the run records private working set, CPU average, queue depth, disk writes, and the exact OS/build/configuration.

The latest deterministic Event Stack benchmark on this Windows 11 25H2 / .NET 10.0.10 / SDK 10.0.302 / Intel i5-1235U host measured `EventStackPage100K` at 4.523 ms mean (99.9% CI 4.381–4.664 ms) with 3.44 MiB managed allocation. Benchmark artifacts are generated under `artifacts/benchmarks` and are intentionally not committed.

The product-level idle gate is measured with `build/quality/Test-ResourceBudget.ps1 -ProcessId <agentPid>,<sessionAgentPid> -DurationSeconds 600`. The monitor records `PROCESS_MEMORY_COUNTERS_EX2.PrivateWorkingSetSize`, working set samples, normalized CPU, and Windows process-I/O write bytes. Queue depth is exposed by the Agent health IPC contract and must be checked alongside the JSON result. The accepted run must cover startup through ten minutes with no bulk events during the final five minutes.

Measured acceptance run on 2026-08-02: Agent PID 41464 plus Session Agent PID 456, 583 samples over 600 seconds, peak combined private working set 14.53125 MiB, peak combined working set 80.7890625 MiB, average normalized CPU 0.0110659393%, and process-I/O writes 989,120 bytes. Both enforced thresholds were false (`PrivateMemoryLimitExceeded=false`, `CpuLimitExceeded=false`). The test configuration used an empty temporary monitoring root to ensure the final five minutes contained no bulk events; the temporary configuration was removed after the run.
