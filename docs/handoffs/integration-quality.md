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
- `build/quality/Test-ResourceBudgetAcceptance.ps1` supervises the existing resource script, proves target PID lifecycle stability and evidence span, and requires the passed schema from the independent `build/quality/New-ResourceQuietWitness.ps1` final-five-minute witness.

## Current verification

- `dotnet build StorageChronicle.slnx --no-restore -v:minimal`: passed, 0 warnings, 0 errors.
- `dotnet run --project tools/StorageChronicle.DocMirrorValidator --no-restore -- .`: passed.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/Test-Fast.ps1 -NoRestore`: passed for all 22 non-privileged test projects.
- `build/Test-WindowsPrivileged.ps1 -Configuration Release -AcceptanceRoot artifacts/acceptance/safe-root`: the latest manifest is `artifacts/acceptance/windows-privileged-20260803-230134.json`; ReadDirectoryChangesW and Session passed, while VHDX/USN/MFT/ETW/SMB/Service/RemovableMedia remained explicitly `NOT_EXECUTED`; the fail-closed overall result was `NOT_EXECUTED` (exit code 2) on this non-administrator Windows 11 host.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/Test-All.ps1`: passed on the fresh 2026-08-03 run. Its coverage phase completed under `artifacts/quality/coverage/runs-20260803-225051`. Domain 80.81%, State 94.13%, Projection 89.4%, Storage 84.51%; EventStack/Diff/Settings ViewModels 73.11%/94.2%/93.41%. The same run completed the full non-privileged Build/Fast/Quality/UI sequence with zero warnings and zero errors in the build phase.
- The supplemental resource supervisor received syntax/prerequisite and deliberate failure-path validation. Its Agent health client now uses cancellable asynchronous named-pipe I/O, and diagnostic mode correctly reports a healthy shortened run as non-acceptance rather than as a harness failure. The actual Release Agent/Session Agent completed a 600-second diagnostic at `artifacts/quality/resources/acceptance-10248-65328-20260803T132953422Z.json`, backed by `process-10248-65328-20260803T132954597Z.json`, with 569 resource samples over 599.8615949 seconds, 596 queue samples, zero missed samples, maximum queue depth 0, stable process identities, peak combined private working set 23.66015625 MiB, and average normalized CPU 0.1997875688%; it remains diagnostic-only because no independent final-five-minute quiet-period evidence was supplied. Benchmark declarations were corrected and bounded storage/rebuild batching was added without reducing the workload. The current bounded-batch matrix completed all six non-MFT suites and 12/12 methods at `artifacts/benchmarks/portable-matrix-current/full-matrix-20260803T122827484Z`; it remains diagnostic because the manifest is `PortableOnly=true` and `AcceptanceEligible=false` until configured MFT evidence exists.

## Open acceptance work

- The privileged Windows lane has only a partial capability run on this workstation; the full external capability manifest remains pending.
- The formal 600-second Agent/Session-Agent acceptance run still requires a non-diagnostic boundary plus independent final-five-minute quiet-period evidence; the current 600-second diagnostic is recorded above. The configured MFT BenchmarkDotNet case is still required after the current integration changes.
- Windows 10 22H2, removable-media insertion, SMB, ETW, service recovery, interactive session, and clean installer update/rollback remain environment-bound.

## Shared-contract requests

None. All current changes use existing platform-neutral contracts or top-agent-owned integration seams.
