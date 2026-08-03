# Completion-gap integration plan

Updated 2026-08-04. This is the top-agent audit of requirements that still need implementation work, external evidence, or final branch integration. An unexecuted acceptance lane is recorded as pending; it is never converted into a pass by a default script.

## Current verdict

The ordinary build and non-privileged tests are green, but Storage Chronicle is not ready for `devenv` or `main`. Ten completion groups remain: three are implementation gaps, five require a Windows acceptance environment or fresh measurements, one requires live correlation capture, and one is the top-agent branch integration sequence.

## Verified in the current worktree

- Full solution build: 0 warnings, 0 errors.
- Source documentation mirrors: DocMirror Validator passed, including required sections, test paths, project READMEs, and orphan checks.
- Critical coverage gate: Domain 80.81%, State 94.13%, Projection 89.4%, Storage 84.51%; the gated UI ViewModels are all at least 70%.
- Non-privileged orchestration: all 22 test projects pass through `build/Test-Fast.ps1`; the privileged project is not counted as a false-green zero-test run.
- Settings modal, Agent settings gateway, Event Stack, and Diff View integration compile and have headless/UI tests.
- Seven ADRs use the required `docs/decisions/ADR-XXXX-<slug>.md` naming.
- ReadDirectoryChangesW handoff is bounded with backpressure, Agent flush interval is clamped to the required 1-60 second range, and all remaining production notification channels are bounded.
- External-media notification overflow emits an explicit continuity-gap item for reconciliation; clipboard overflow is limited to its transient correlation buffer.
- Unix-like wrappers exist for the required build and fast-test entry points. Missing mirrors for the privileged script and MFT benchmark were added.
- Added the R-00 sections 8-9 deterministic metadata-only correlation fixture and acceptance test. It reports process attribution `Exact=1/3`, `Correlated=1/3`, `Unknown=1/3`, and Explorer source correlation `1/3`; the test also verifies that transient Clipboard/ETW observations do not enter durable history.
- Made Agent and Projection process-property lookup case-insensitive so Normalizer safe-property canonicalization preserves recorded parent Process Instance links.
- R-03/R-19 audit found the focused performance entry point selected only `StorageChronicleBenchmarks`; previous artifacts contained only `EventStackPage100K`. Added `build/quality/Test-FullBenchmarkMatrix.ps1` with per-suite JSON/method validation and fail-closed MFT eligibility. The current bounded-batch run completed all six non-MFT suites and all 12 expected methods at `artifacts/benchmarks/portable-matrix-current/full-matrix-20260803T122827484Z`; it is diagnostic evidence only because `PortableOnly=true` and `AcceptanceEligible=false`. The configured MFT capability benchmark remains pending.
- R-03/R-19 audit found the existing resource gate lacked an independent lifecycle/timestamp-span/quiet-period evidence supervisor. Added `build/quality/Test-ResourceBudgetAcceptance.ps1`; it wraps the existing script without replacing it. The IPC client now uses cancellable asynchronous named-pipe I/O, diagnostic mode records a healthy shortened run without converting intentional ineligibility into a failure, and the actual Release Agent/Session Agent completed a 600-second diagnostic with 569 resource samples over 599.8615949 seconds, 596 queue samples, zero missed queue samples, maximum queue depth 0, stable process identities, peak combined private working set 23.66015625 MiB, and average normalized CPU 0.1997875688%. The run remains diagnostic-only (`AcceptanceEligible=false`) because independent final-five-minute quiet-period evidence was not supplied and the boundary was intentionally marked diagnostic; the formal acceptance gate remains pending.
- The requirements audit removed the unsupported `LangVersion=preview` override; the repository now uses the .NET 10 SDK default language version. An opt-in `build/Test-AotCompatibility.ps1` publication configuration now covers only Agent and Session Agent, and `docs/installer/signing-procedure.md` records the separate unsigned-development and signed-release procedures without weakening SmartScreen or security boundaries.

## Unmet requirements and exit evidence

| Gap | Requirements | Required exit evidence | Current state |
|---|---|---|---|
| Windows privileged capability matrix | R-03, R-13, R-19 | `build/Test-Privileged.ps1` manifest with VHDX, USN, MFT, ReadDirectoryChangesW, ETW, SMB, SCM/service, session, and removable-media capabilities executed or explicitly blocked by a named capability | Latest Release run at `artifacts/acceptance/windows-privileged-20260803-230134.json`: ReadDirectoryChangesW and Session passed; VHDX/USN/MFT/ETW/SMB/Service/RemovableMedia remained `NOT_EXECUTED`; overall exit code 2 |
| Windows 10 22H2 compatibility | R-00, R-08, R-13, R-19 | Clean Windows 10 22H2 run of the defined matrix, with OS/build/date recorded in `docs/release/windows10-compatibility.md` | Pending; current document records boundary verification only |
| Idle resource gate | R-03 section 6, R-19 | Current Agent and Session Agent PIDs, queue depth, disk-write samples, private working set, and CPU samples proving CPU <=0.5% and private working set <50 MiB | 600-second diagnostic completed at `artifacts/quality/resources/acceptance-10248-65328-20260803T132953422Z.json` with complete resource/queue sampling and thresholds not exceeded; formal acceptance remains pending because `AcceptanceEligible=false` and final-five-minute quiet-period evidence is absent |
| Full performance matrix | R-03 section 5, R-19 | Release BenchmarkDotNet reports for 100K Event Stack, 1M state/path reconstruction, append/Zstandard/SQLite/media workloads, large-folder move, and configured MFT capability | Current-code portable matrix passed all six non-MFT suites and 12/12 methods; `AcceptanceEligible=false` until a configured MFT capability run is completed |
| Installer acceptance | R-20 | Windows 10/11 clean install, repair, update, rollback, uninstall, service/session, non-admin, storage-permission, and history-retention evidence | WiX/MSI build and manifest verified; physical matrix pending |
| Process and Explorer correlation measurements | R-00 sections 8-9 | Fixed acceptance fixtures with measured Exact/Correlated/Unknown process attribution and Explorer source-correlation rates | Deterministic fixture measured (Exact 1/3, Correlated 1/3, Unknown 1/3; Explorer 1/3); live Agent/Explorer capture remains `NOT_EXECUTED` |
| User-confirmed reconciliation execution | R-08, R-13, R-17, TOP_CODEX.md | A confirmed request must run the selected NTFS MFT or non-NTFS directory reconciliation, append only discovered differences as `ReconciliationDiscovered`, preserve unknown time/process quality, and resume normal monitoring | The prompt and decline path exist, but `NamedPipeAgentServer` currently handles Execute by restarting monitoring; no selected-volume MFT/directory reconciliation runner is wired |
| NTFS reconciliation candidate metadata and I/O policy | R-00, R-13, R-17 | Initial public MFT enumeration must feed standard metadata; protected enumeration must scope `SeBackupPrivilege`; reconciliation must detail only changed candidates and use OS low-priority mode | Initial metadata now routes through the production directory snapshot reader and the pre-scan USN boundary is recovered; candidate-only detailed metadata, scoped backup-privilege enablement, and low-priority reconciliation I/O remain unimplemented |
| Branch/worktree integration | R-02 and TOP_CODEX.md | Clean reviewed feature commit, `--no-ff` merge into `devenv`, final comprehensive gates, then `--no-ff` merge into `main` | Feature branch is clean at the latest reviewed commit; `devenv`/`main` integration remains pending |

## Execution plan and completion gates

1. Implement and test the selected-volume reconciliation runner, NTFS initial metadata provider wiring, scoped backup privilege, and low-priority reconciliation I/O. Keep source facts and quality explicit; do not make the prompt or test fakes stand in for the real operation.
2. Review the current feature worktree. Exclude generated outputs and machine-specific files, stage only source/tests/docs/scripts, confirm every source mirror, then commit the reviewed feature change.
3. On a disposable Windows 11 acceptance host, run the complete privileged capability matrix. Use isolated test media and preserve the manifest for both executed and explicitly blocked capabilities.
4. On Windows 10 22H2, run the compatibility matrix covering capability detection, service, Avalonia, NTFS/USN/MFT, ETW, Clipboard, SMB/Share, cloud placeholder, removable media, and installer behavior. Record only executed evidence.
5. Run the current Agent and Session Agent through the supplemental supervised 600-second resource gate with quiet-period evidence. Then run the configured MFT capability case to extend the already completed current-code portable BenchmarkDotNet matrix to a release-eligible matrix.
6. Run fixed process and Explorer correlation fixtures and record Exact/Correlated/Unknown plus the Explorer source-correlation rate. Keep source facts distinct from correlation and UI interpretation.
7. Execute installer acceptance on Windows 10 and 11: clean install, repair, update, rollback, uninstall with history retention, LocalSystem service recovery, Session Agent, normal-user UI, non-admin behavior, and storage-permission failures.
8. Fix every failure without weakening record quality or hiding unexecuted acceptance. Rerun the non-privileged suite, privileged suite, quality/coverage gates, UI tests, benchmark/resource gates, and documentation validator.
9. Only after all verification rows have executable evidence, merge the reviewed feature into `devenv` with `--no-ff`; rerun the final comprehensive suite on the integrated branch; then merge `devenv` into `main` with `--no-ff` and update readiness documents.

## Non-negotiable invariants during the plan

No file contents or content hashes; no history-deletion behavior; no synthetic descendant events for directory operations; no fake data standing in for missing acceptance; no duplicate shared contracts; no unapproved changes under `Requirements/`; no merge from a dirty or unreviewed worktree; and no default test result that hides an unexecuted privileged lane.
