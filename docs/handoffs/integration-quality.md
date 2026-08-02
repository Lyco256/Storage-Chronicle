# Integration-quality handoff

## Scope completed

This branch changes only the assigned integration-quality paths: ArchUnitNET/xUnit v3 architecture gates, Avalonia.Headless.XUnit UI coverage, Golden/E2E fixtures against the real append log and SQLite rebuild, BenchmarkDotNet workloads, metadata-only test-data/log tools, quality scripts, and their documentation.

No product source or shared contract was changed by this branch. Existing unrelated worktree changes remain unstaged.

## Implemented gates

- ArchUnitNET checks domain/contracts dependency direction and UI isolation.
- Headless Avalonia opens the real desktop `MainWindow` and verifies its navigation surface.
- Golden/E2E covers create/delete replay, restart, SQLite index deletion/rebuild, final canonical operation, and explicit capacity-stop behavior.
- Benchmarks cover 1M identity import/path reconstruction and 100K grouping, Event Stack, diff, append serialization, real Zstandard preparation, SQLite-shaped indexing, and media manifest reconciliation with `MemoryDiagnoser`.
- Test-data generation is deterministic, bounded, metadata-only, and supports NDJSON/Golden output. Log inspection is bounded and content-free.
- `StorageChronicle.ResourceMonitor` records measured private bytes and normalized CPU and returns nonzero when the 50 MiB/0.5% defaults are exceeded. It explicitly does not fabricate disk-write results.
- Windows privileged execution is isolated to `Category=WindowsPrivileged`; non-Windows hosts skip that acceptance-only lane.
- CodeCoverage references `Microsoft.Testing.Extensions.CodeCoverage` and emits Cobertura artifacts for the three owned test projects.

## Verification run

Successful on this worktree with zero build warnings/errors:

- `dotnet restore` for Architecture, UI Headless, EndToEnd, Benchmarks, and ResourceMonitor projects with `--ignore-failed-sources`.
- `dotnet build` for those projects with `--no-restore --nologo`.
- Direct xUnit v3 executable: Architecture, UI Headless, and EndToEnd; all passed (5, 2, and 3 tests respectively).

The standard `dotnet test` command is currently blocked by the pre-existing repository `global.json`, which does not select the .NET 10 `Microsoft.Testing.Platform` runner while MTP 2.3.0 is referenced. The CodeCoverage script therefore fails loudly until the integration owner adds the required runner selection; no coverage or performance acceptance result is claimed here. This branch does not edit `global.json` because it is outside the assigned edit paths.

## Commands for the integration owner

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build/quality/Test-Quality.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File build/quality/Test-Privileged.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File build/quality/Test-Performance.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File build/quality/Test-ResourceBudget.ps1 -ProcessId <agent-or-session-agent-pid>
```

Expected artifacts are under `artifacts/quality/`, `artifacts/benchmarks/`, and `artifacts/quality/resources/`; generated artifacts are not committed.

## Known limitations

Physical Windows volume/media, USN/MFT/ETW/service privilege, release-hardware CPU/memory, and disk-write acceptance remain acceptance-host measurements. The benchmark suite provides reproducible workloads but does not substitute for those measurements.
