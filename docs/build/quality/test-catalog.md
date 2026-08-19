# Integration-quality test catalog

| Category | Runner | Scope | Host boundary |
| --- | --- | --- | --- |
| Architecture | ArchUnitNET + xUnit v3 | dependency directions and project boundaries | all hosts |
| UI headless | Avalonia.Headless.XUnit | real desktop shell and view surface | all hosts |
| Golden/E2E | xUnit v3 | real append log, SQLite rebuild, restart, capacity failure | all hosts |
| WindowsPrivileged | xUnit v3 trait | USN/MFT/ETW/media/service acceptance | Windows acceptance host only |
| Coverage | Microsoft.Testing.Extensions.CodeCoverage | Cobertura instrumentation for owned test projects | MTP runner required |
| Performance | BenchmarkDotNet | 1M/100K functional-shaped workloads | representative hardware |
| Resource budget | ResourceMonitor | private memory and CPU samples for a running process | target process required |
| Correlation metrics | xUnit v3 + `Test-CorrelationMetrics.ps1` | deterministic R-00 process attribution and Explorer source-correlation fixture | fixture-only mode is portable; live capture requires acceptance host |
| Real-I/O oracle | xUnit v3 + `StorageChronicle.RealIoOracleValidator` | exact parent/name path coverage, operation semantics, FileId continuity, delete metadata, and final-state comparison | synthetic history tests are negative/contract tests; eligible evidence requires a real TestLab workload and Agent history |

No gate stores file contents or content hashes. Failure, cancellation, restart, corruption, capacity, and recovery paths remain explicit test cases rather than synthetic fixtures.
