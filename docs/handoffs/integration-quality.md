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

## Output-safety remediation (2026-10-03, `feat/integration-quality-r37`)

### Owned changes

- `tools/StorageChronicle.TestDataGenerator/Program.cs`: `--output` now rejects empty paths, UNC/device paths, drive-relative and alternate-data-stream paths, `.`/`..` segments, missing parent directories, and reparse-point ancestors. It does not create parent directories. The final file is opened with `FileMode.CreateNew`, `FileAccess.Write`, and `FileShare.None`; an existing target is rejected without replacement. Output-path and filesystem errors return exit code 1.
- `tests/StorageChronicle.Architecture.Tests/`: added a project reference and CLI tests covering successful new-file creation, preservation of an existing sentinel, missing-parent refusal, traversal refusal, and reparse-point-parent refusal (link test returns without exercising the assertion if the host cannot create a symlink).
- Matching tool/test documentation mirrors were updated/added.

`CreateNew` prevents replacement of an existing final target. It does **not** prove that the caller-selected directory is an approved, isolated evidence root, and checking parent components cannot eliminate every concurrent parent-path race. The CLI has no evidence-root authorization contract; callers remain responsible for selecting an approved location. Physical/product/privileged workflows were not run.

### Top-agent handoff request — `RealIoOracleValidator`

Ownership boundary: `tools/StorageChronicle.RealIoOracleValidator/**` and its test project are top-agent-owned/unassigned for this task. No changes were made there.

The static audit reported that `tools/StorageChronicle.RealIoOracleValidator/Program.cs` creates the caller-supplied output's parent directory and writes via `WriteAllTextAsync` (previously observed around lines 338–343), so an existing caller-selected file can be overwritten. Please remediate within the top-owned paths:

1. Accept only an evidence output path that is proven to be a new direct child of a run-specific evidence root created/authorized by the harness; establish the root/run identity and reject traversal, UNC/device paths, reparse points, and volume/root escape before writing.
2. Require the output target not to exist and use exclusive create-new semantics; never create arbitrary parent directories from the untrusted output argument and never replace an existing target. Preserve partial evidence on write failure rather than deleting an ambiguous target.
3. Add focused tests for successful new evidence, existing-target preservation, invalid/missing root, traversal/root escape, reparse parents, I/O failure, and recovery/cancellation where applicable. Do not treat `CreateNew` by itself as proof of root isolation.

### Verification on this branch

- `dotnet restore StorageChronicle.Architecture.Tests/StorageChronicle.Architecture.Tests.csproj --nologo`: passed.
- Initial focused `dotnet test ... -c Debug --no-restore`: rejected by repo policy because `global.json` requires Microsoft.Testing.Platform and this command selected VSTest. The matching focused `dotnet build` initially needed restore assets; after restore, the focused build succeeded with 0 warnings/errors.
- Before building the full Debug solution, three pre-existing architecture tests could not load dependency assemblies in this fresh worktree; after the full Debug build, the Architecture test executable passed 12/12, including all five output-safety scenarios.
- `dotnet restore StorageChronicle.slnx --nologo`: passed.
- `dotnet build StorageChronicle.slnx -c Debug --no-restore --nologo`: passed, 0 warnings, 0 errors.
- `tests/StorageChronicle.Architecture.Tests/bin/Debug/net10.0/StorageChronicle.Architecture.Tests.exe --progress off --minimum-expected-tests 1 --filter-not-trait 'Category=WindowsPrivileged'`: passed, 12/12.
- `dotnet run --project tools/StorageChronicle.DocMirrorValidator --no-restore -- .`: passed.
- `./build/Test-Fast.ps1 -NoRestore`: passed all 24 discovered non-privileged test projects; all reported 0 failures. The test suite includes safe unit/contract tests for the separately owned Oracle Validator, but no source in that area was edited. No physical/product/privileged workflow was run.

### Limitations

- This handoff does not resolve the Oracle Validator overwrite finding; the top-agent request above remains open.
- This change does not establish or authorize an evidence root. It only makes TestDataGenerator output new-only and rejects unsafe path forms observed before the create operation.
- Changes are committed on this feature branch. No merge, rebase, or push has been performed.

## Req19 physical MFT acceptance migration (2026-10-04)

### Changes

- Replaced the full matrix's Windows 11 TestLab guest and VM CPU/memory requirements with physical Windows host fields and a read-only seed identity contract in `build/quality/MftPhysicalSeed.Contracts.ps1`.
- The live inventory queries the attached VHDX through `Get-VHD`, maps its disk and partitions to the requested drive-letter device path, reads disk/volume identities and NTFS label, verifies the persistent marker/run GUID, checks system/boot/recovery/pagefile/crash-dump exclusions, and records host OS/build/CPU/memory plus VHDX type/size/path and identity facts. It performs no disk/volume/VHDX writes.
- Updated `WindowsMftBenchmarks` to require the physical preflight schema and matching identity values and to emit physical environment fields instead of guest CPU/memory fields. Updated the final acceptance gate to require and cross-check the live preflight identity evidence and actual one-million-entry counters.
- Added `Test-MftPhysicalSeedContracts.ps1` with 22 non-privileged in-memory/static cases and integrated it into `Test-Quality.ps1`.
- Updated mirrors for the benchmark, full matrix, final acceptance, quality runner, and added mirrors for the inventory and contract test.

### Safety limit — acceptance deliberately remains blocked

This ownership scope has no audited create-new VHDX/seed workflow that can prove the VHDX pathname was absent before creation within a user-approved isolation root. A persistent marker alone cannot prove that provenance. Therefore `Test-FullBenchmarkMatrix.ps1 -IncludeMft` performs no BenchmarkDotNet invocation and writes `NOT_EXECUTED`, even when read-only identity checks would pass. No MFT volume was queried; no VHDX was created, attached, formatted, or changed; no benchmark/product/service/installer was started. The top agent must provide and review the authorized seed-creation/provenance contract before the MFT lane can become executable. This is not a completed R-03 acceptance run.

### Verification

- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/quality/Test-MftPhysicalSeedContracts.ps1`: passed 22/22; uses only in-memory inventories and reads source text.
- PowerShell parser validation of the inventory, contract test, full matrix, final gate, and quality runner: passed.
- `dotnet build benchmarks/StorageChronicle.Benchmarks/StorageChronicle.Benchmarks.csproj -c Debug --no-restore --nologo`: passed, 0 warnings/errors.
- `dotnet run --project tools/StorageChronicle.DocMirrorValidator --no-restore -- .`: passed.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/quality/Test-Quality.ps1`: passed DocMirror, Architecture, Integration, UI Headless, MFT seed contracts (13/13 at the time of the full gate run), Coverage, and VirtualBox legacy contract validation. The focused contract suite was expanded to 22/22 and rerun afterward.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/Test-Fast.ps1 -NoRestore`: passed all configured non-privileged tests; per-project build warnings/errors were zero. Some fixture tools intentionally print their rejected-input diagnostics to stderr while their contract tests pass.
- No merge or push was performed.
