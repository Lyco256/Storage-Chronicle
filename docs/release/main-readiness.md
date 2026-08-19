# Main readiness

The repository is not yet ready for `main`. The top-agent merge sequence remains `feat/*` review -> `devenv` with `--no-ff` -> final gates -> `main` with `--no-ff`.

The branch/ref audit for this continuation is recorded in `docs/release/branch-audit-2026-08-19.md`; local `devenv` and `main` exist, while the remote currently exposes only `origin/feat/benchmark-performance`.

## Verified on 2026-08-03

- `dotnet build StorageChronicle.slnx --no-restore -v:minimal`: 0 warnings, 0 errors.
- `build/Test-Fast.ps1 -NoRestore`: all 22 non-privileged test projects passed.
- `dotnet run --project tools/StorageChronicle.DocMirrorValidator --no-restore -- .`: passed.
- `build/quality/Test-Coverage.ps1`: passed the required 80%/70% thresholds with current measured rates recorded in `docs/handoffs/integration-quality.md`.
- Settings UI unit and headless tests: 13 passed.

## Blocking release evidence

- The latest partial `build/Test-WindowsPrivileged.ps1` Release run is recorded at `artifacts/acceptance/windows-privileged-20260803-230134.json`. It passed ReadDirectoryChangesW and Session on a safe existing directory, but VHDX, USN, MFT, ETW, SMB, service, and removable-media capabilities remained `NOT_EXECUTED`; the overall fail-closed exit code was 2.
- Windows 10 22H2 compatibility, physical media insertion, ETW, service recovery, and clean installer/update/rollback are not available in this current environment.
- The feature checkout is clean at the latest reviewed commit, but it has not yet been merged into `devenv`; no `main` merge is authorized.
- A post-integration 600-second resource-budget diagnostic completed with complete lifecycle/resource/queue evidence, but the formal acceptance run remains pending because it was intentionally diagnostic and lacks independent final-five-minute quiet-period evidence. Live Agent/Explorer correlation measurements also remain pending; the deterministic correlation fixture is measured at Exact 1/3, Correlated 1/3, Unknown 1/3, Explorer 1/3.
- The current bounded-batch BenchmarkDotNet matrix completed all six non-MFT suites and 12/12 methods under `artifacts/benchmarks/portable-matrix-current/full-matrix-20260803T122827484Z`; its manifest remains `AcceptanceEligible=false` because no configured MFT capability run was supplied. The MFT suite and the supervised resource gate must still run before performance acceptance can be considered.
- The R-03/R-19 supplemental gates are available at `build/quality/Test-FullBenchmarkMatrix.ps1` and `build/quality/Test-ResourceBudgetAcceptance.ps1`. The resource wrapper's 600-second diagnostic mode has executed, but its formal acceptance mode and the configured MFT matrix acceptance mode have not; no new success is implied by diagnostic evidence alone.

## Additional implementation blockers found in the 2026-08-04 audit

- The confirmed Execute path, selected-volume NTFS/public-MFT route, non-NTFS directory route, candidate-only metadata reads, durable Source/Canonical/State ordering, cancellation/failure gaps, scoped `SeBackupPrivilege`, and low-priority I/O telemetry are now implemented on the feature branch and covered by 20 Agent tests. They still require the real Windows TestLab capability matrix before acceptance can be marked verified.
- The Windows TestLab scripts and real metadata-only file-mutation workload are now present with fail-closed root/VM/VHDX/marker checks. The current host preflight remains blocking: Windows 11 Home, no Hyper-V PowerShell module/VMMS, and no approved local ISO/TestLab root.
- R-17 IPC role separation is now implemented and tested: `--diagnostic` skips service registration/recovery configuration, both clients send a versioned role/session hello, the Agent verifies the authenticated process/session, limits Session Agent connections to ClipboardCandidate, and rejects an unpublished Session Agent role.
- The shared build language-version override and release-signing procedure gaps were corrected in the current feature branch; Native AOT configuration is now opt-in and remains non-acceptance diagnostic work.

No release document may say these items are verified until the corresponding acceptance artifacts exist. History retention, no-driver MVP, no-content/no-hash, and no-synthetic-descendant invariants remain mandatory in every acceptance run.

## Requirements 21–31 audit

The final-acceptance requirements are now represented by executable orchestration and evidence contracts, but none of the environment-bound rows below is claimed as passed without its measured artifact:

- confirmed reconciliation execution: implementation tests passed; real NTFS/non-NTFS TestLab run pending;
- privileged Windows matrix and Windows 10 22H2: not executed on this host;
- formal 600-second dual-process resource gate with independent quiet-period evidence: not executed;
- dedicated `SC_TEST_MFT_VOLUME` 10K/100K/1M matrix: harness expanded, volume/marker run pending;
- physical installer and real Agent/Explorer correlation: not executed;
- `devenv`/`main` integration: intentionally pending until every blocking gate has an eligible artifact.
