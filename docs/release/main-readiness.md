# Main readiness

The repository is not yet ready for `main`. The top-agent merge sequence remains `feat/*` review -> `devenv` with `--no-ff` -> final gates -> `main` with `--no-ff`.

The branch/ref audit for this continuation is recorded in `docs/release/branch-audit-2026-08-19.md`; local `devenv` and `main` exist, while the remote currently exposes only `origin/feat/benchmark-performance`.

`build/quality/Test-FinalAcceptance.ps1` is the final evidence aggregator. It requires nine real, eligible artifacts and exits with `2` when any artifact is missing, diagnostic, partial, failed, or `AcceptanceEligible=false`; the no-argument verification on 2026-08-19 correctly remained blocked.

## Verified on 2026-08-20

- `dotnet build StorageChronicle.slnx --no-restore -v:minimal`: 0 warnings, 0 errors.
- `build/Test-Fast.ps1 -NoRestore`: all 22 non-privileged test projects passed.
- `build/Test-All.ps1` (2026-08-20, default non-privileged lane): build, Fast, quality/coverage, and UI stages passed with exit code 0; privileged Windows acceptance remains intentionally isolated.
- `dotnet run --project tools/StorageChronicle.DocMirrorValidator --no-restore -- .`: passed.
- `build/quality/Test-Coverage.ps1`: passed the required 80%/70% thresholds with current measured rates recorded in `docs/handoffs/integration-quality.md`.
- Settings UI unit and headless tests: 13 passed.

## Blocking release evidence

- `build/Test-Privileged.ps1` now emits a v2 manifest with the complete required capability list and per-run environment/capability/oracle/source/canonical/state/reconciliation/service/error/result artifacts. It refuses USN journal creation/resizing and remains fail-closed: the current host cannot execute the required matrix, so no capability is promoted to acceptance by the new artifact shape.
- Windows 10 22H2 compatibility, physical media insertion, ETW, service recovery, and clean installer/update/rollback are not available in this current environment.
- The feature checkout is clean at the latest reviewed commit, but it has not yet been merged into `devenv`; no `main` merge is authorized.
- A post-integration 600-second resource-budget diagnostic completed with complete lifecycle/resource/queue evidence, but the formal acceptance run remains pending because it was intentionally diagnostic and lacks independent final-five-minute quiet-period evidence. Live Agent/Explorer correlation measurements also remain pending; the deterministic correlation fixture is measured at Exact 1/3, Correlated 1/3, Unknown 1/3, Explorer 1/3.
- The current bounded-batch BenchmarkDotNet matrix completed all six non-MFT suites and 12/12 methods under `artifacts/benchmarks/portable-matrix-current/full-matrix-20260803T122827484Z`; its manifest remains `AcceptanceEligible=false` because no configured MFT capability run was supplied. The MFT suite and the supervised resource gate must still run before performance acceptance can be considered.
- The R-03/R-19 supplemental gates are available at `build/quality/Test-FullBenchmarkMatrix.ps1` and `build/quality/Test-ResourceBudgetAcceptance.ps1`. The resource wrapper's 600-second diagnostic mode has executed, but its formal acceptance mode and the configured MFT matrix acceptance mode have not; no new success is implied by diagnostic evidence alone.

## Additional implementation blockers found in the 2026-08-04 audit

- The confirmed Execute path, selected-volume NTFS/public-MFT route, non-NTFS directory route, candidate-only metadata reads, durable Source/Canonical/State ordering, cancellation/failure gaps, scoped `SeBackupPrivilege`, low-priority I/O telemetry, and a bounded post-commit live-event reconciliation buffer are implemented on the feature branch and covered by 26 Agent tests. They still require the real Windows TestLab capability matrix before acceptance can be marked verified.
- The Windows TestLab scripts and real metadata-only file-mutation workload are now present with fail-closed root/VM/VHDX/marker checks. The current host preflight remains blocking: Windows 11 Home, no Hyper-V PowerShell module/VMMS, and no approved local ISO/TestLab root.
- R-17 IPC role separation is now implemented and tested: `--diagnostic` skips service registration/recovery configuration, both clients send a versioned role/session hello, the Agent verifies the authenticated process/session, limits Session Agent connections to ClipboardCandidate, and rejects an unpublished Session Agent role.
- The shared build language-version override and release-signing procedure gaps were corrected in the current feature branch; Native AOT configuration is now opt-in and remains non-acceptance diagnostic work.

## Additional audit findings on 2026-08-20

- A continuity-failure normalization defect was found and fixed: a reconciliation-origin `UnverifiedGap` was previously rewritten as `ReconciliationDiscovered`. The normalizer now preserves explicit gap operation/quality first, and `ConfirmedReconciliationRunnerTests.NonNtfsSnapshotGapIsFailedAndRecordedInsteadOfCompleted` covers the fail-closed path; the fast lane is 26 Agent tests with zero failures.
- The MFT matrix gate was strengthened. `build/quality/Test-FullBenchmarkMatrix.ps1 -IncludeMft` now requires `StorageChronicle.MftBenchmarkEvidence.v1` with per-run dataset/enumeration/candidate/detail-query/canonical/drop counters and OS/build/VM/VHDX fields. The existing BenchmarkDotNet method reports alone cannot satisfy this gate, so the connected TestLab/product evidence remains blocking.
- The final aggregator now validates group-specific schemas and required fields, including all required privileged capability rows, all eleven installer cases, connected MFT evidence, live correlation with `FalseExactCount=0`, and branch state. A generic JSON with `AcceptanceEligible=true` cannot bypass the final gate.
- TestLab VHDX cleanup now revalidates both guest markers for the current TestId/Role before deletion; VHDX creation also rolls back an exact newly-created image if attachment/manifest recording fails. These safety changes do not execute a TestLab run and do not close any environment-bound requirement.
- The remaining static acceptance gaps are real: `Test-Privileged.ps1` still hard-codes Reconciliation, ETW full correlation, ReadDirectoryChangesW gap, SMB mutation, Service lifecycle, Session Agent, Volume GUID, hot attach/detach, ACL/SeBackupPrivilege, and Non-NTFS capabilities as not wired to the product matrix; `Invoke-WindowsTestLab.ps1` still executes only the real mutation workload/oracle, not the Agent/source/canonical/state/reconciliation integration. These must be implemented and run before any acceptance claim.

No release document may say these items are verified until the corresponding acceptance artifacts exist. History retention, no-driver MVP, no-content/no-hash, and no-synthetic-descendant invariants remain mandatory in every acceptance run.

## Requirements 21–31 audit

The final-acceptance requirements are now represented by executable orchestration and evidence contracts, but none of the environment-bound rows below is claimed as passed without its measured artifact:

- confirmed reconciliation execution: implementation tests passed; real NTFS/non-NTFS TestLab run pending;
- privileged Windows matrix and Windows 10 22H2: not executed on this host;
- formal 600-second dual-process resource gate with independent quiet-period evidence: not executed;
- dedicated `SC_TEST_MFT_VOLUME` 10K/100K/1M matrix: harness expanded, volume/marker run pending;
- MFT benchmark correctness artifact: the acceptance gate now validates the required per-run dataset/candidate/detail-query/drop/environment oracle, but the current BenchmarkDotNet method harness does not produce the connected product/TestLab artifact; this remains blocking until that workload is connected;
- physical installer and real Agent/Explorer correlation: not executed; the correlation wrapper intentionally reports fixture-only or live `NOT_EXECUTED` and has no synthetic Explorer substitute;
- physical acceptance bundles are now generated by `build/package/New-ManualAcceptanceBundle.ps1`; the generator refuses missing updated/rollback MSI inputs and generated bundles remain `AcceptanceEligible=false` until real-machine evidence exists;
- `devenv`/`main` integration: intentionally pending until every blocking gate has an eligible artifact.
