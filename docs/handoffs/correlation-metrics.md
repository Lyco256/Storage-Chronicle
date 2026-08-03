# Correlation metrics handoff

## Scope

R-00 sections 8–9 process attribution and Explorer source-correlation acceptance evidence. Requirements and shared contracts were not changed. The work was created in the dedicated `feat/correlation-metrics` worktree at `C:\Users\lyco2\OneDrive\ドキュメント\Storage Chronicle-correlation-metrics`.

## Changes

- Added `tests/StorageChronicle.CorrelationAcceptance.Tests` with a fixed metadata-only JSON fixture.
- The fixture runs through `AgentPipeline`, `EventNormalizer`, Session Agent clipboard metadata conversion, and `ProjectionService`.
- The test verifies source/canonical separation: ETW read and Clipboard facts are transient and are not durable file-history rows.
- The test emits and fixes deterministic results: process `Exact=1/3`, `Correlated=1/3`, `Unknown=1/3`; Explorer candidates `3`, correlated `1`, uncorrelated `2`, rate `0.3333`; one non-Explorer create is excluded.
- Fixed Agent and Projection process-property lookup so Normalizer safe-property canonicalization preserves parent Process Instance navigation.
- Added `build/quality/Test-CorrelationMetrics.ps1`, its mirror, test documentation, and solution registration.

## Validation

The current top-agent validation supersedes the historical project-level count below: `build/Test-Fast.ps1 -NoRestore` passed all 22 non-privileged projects, and the current Agent test count is 13. The MTP module invocation is used to avoid a project-level false zero-test result under the .NET 10 runner.

- `dotnet restore StorageChronicle.slnx --nologo` — passed.
- `dotnet build StorageChronicle.slnx --no-restore -c Release -v:minimal` — passed, 0 warnings, 0 errors.
- `dotnet build StorageChronicle.slnx --no-restore -v:minimal` — passed, 0 warnings, 0 errors.
- `dotnet test StorageChronicle.slnx --no-build --no-restore` — passed, 175 passed, 0 failed, 0 skipped.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build\quality\Test-CorrelationMetrics.ps1 -FixtureOnly` — passed and printed the fixed metrics.
- Default `Test-CorrelationMetrics.ps1` — deliberately returned exit code `2` with `LIVE_MACHINE_MEASUREMENT=NOT_EXECUTED` after the fixture passed.
- Missing fixture invocation — deliberately returned exit code `2` with `CORRELATION_METRICS status=NOT_EXECUTED`.
- `dotnet run --project tools/StorageChronicle.DocMirrorValidator/StorageChronicle.DocMirrorValidator.csproj -c Release --no-build --no-restore -- .` — passed.
- Existing Normalization, Agent, and Projection test projects — passed: 22, 13, and 13 tests respectively in the current integrated workspace.
- Fixture forbidden-property scan and `git diff --check` — passed; only Git's expected LF/CRLF normalization notices were emitted.

## Limitations

No live Agent/interactive Session Agent/Explorer capture was run. The script keeps this as `NOT_EXECUTED` and exits nonzero unless the caller explicitly selects `-FixtureOnly`. The deterministic fixture result must not be presented as a live-machine rate.
