# Integration and quality handoff

## Scope

Architecture tests, headless UI tests, golden and end-to-end fixtures, CodeCoverage, documentation validation, resource-budget tooling, benchmarks, and test orchestration.

## Implemented gates

- `Directory.Build.props` enables Microsoft Testing Platform and XML documentation for non-test source projects.
- `Directory.Build.targets` supplies the MTP CodeCoverage extension to test projects without duplicating package references.
- `StorageChronicle.DocMirrorValidator` checks mirrors, required file-role sections, test paths, project READMEs, and orphan source mirrors.
- `build/Test-Fast.ps1` builds and runs all 22 non-privileged test modules synchronously; `StorageChronicle.Platform.Windows.Integration.Tests` is a separate explicit acceptance lane.
- `build/quality/Test-Coverage.ps1` aggregates the critical matrix and enforces Domain/State/Projection/Storage at 80% and each UI ViewModel at 70%.
- `build/Test-Privileged.ps1` and `build/Test-WindowsPrivileged.ps1` fail closed when VHDX/media/SMB/service/session prerequisites are absent.
- `build/quality/Test-FullBenchmarkMatrix.ps1` supplements the focused performance script with separately logged, fail-closed lanes for every R-03 benchmark method and requires an explicitly configured MFT capability volume for acceptance eligibility.
- `build/quality/Test-ResourceBudgetAcceptance.ps1` supervises the existing resource script, proves target PID lifecycle stability and evidence span, and requires external final-five-minute quiet-period evidence.

## Current verification

- `dotnet build StorageChronicle.slnx --no-restore -v:minimal`: passed, 0 warnings, 0 errors.
- `dotnet run --project tools/StorageChronicle.DocMirrorValidator --no-restore -- .`: passed.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/Test-Fast.ps1 -NoRestore`: passed for all 22 non-privileged test projects.
- `build/Test-Privileged.ps1 -AcceptanceRoot artifacts/acceptance/safe-root`: ReadDirectoryChangesW and Session passed; the fail-closed overall result was `NOT_EXECUTED` (exit code 2) because this non-administrator Windows 11 Home host has no VHDX, USN/MFT, ETW, SMB, service, or removable-media prerequisites.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/quality/Test-Coverage.ps1 -Configuration Debug`: passed on the fresh 2026-08-03 run under `artifacts/quality/coverage/runs-20260803-220150`. Domain 80.81%, State 94.13%, Projection 89.15%, Storage 84.51%; EventStack/Diff/Settings ViewModels 73.11%/94.2%/93.41%.
- The supplemental resource supervisor received syntax/prerequisite and deliberate failure-path validation. Its Agent health client now uses cancellable asynchronous named-pipe I/O, and diagnostic mode correctly reports a healthy shortened run as non-acceptance rather than as a harness failure. A current 15-second diagnostic with actual Release Agent/Session Agent processes recorded complete queue sampling with zero missed samples, while remaining diagnostic-only. Benchmark declarations were corrected and bounded storage/rebuild batching was added without reducing the workload. The current bounded-batch matrix completed all six non-MFT suites and 12/12 methods at `artifacts/benchmarks/portable-matrix-current/full-matrix-20260803T122827484Z`; it remains diagnostic because the manifest is `PortableOnly=true` and `AcceptanceEligible=false` until configured MFT evidence exists.

## Open acceptance work

- The privileged Windows lane has only a partial capability run on this workstation; the full external capability manifest remains pending.
- A fresh 600-second Agent/Session-Agent resource run and the configured MFT BenchmarkDotNet case are still required after the current integration changes.
- Windows 10 22H2, removable-media insertion, SMB, ETW, service recovery, interactive session, and clean installer update/rollback remain environment-bound.

## Shared-contract requests

None. All current changes use existing platform-neutral contracts or top-agent-owned integration seams.
