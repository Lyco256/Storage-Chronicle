# Requirements verification

`Requirements/98_REQUIREMENTS_COVERAGE.md` remains authoritative. The table below assigns one verification ID to every row in that coverage document; it records executable evidence and does not weaken any product requirement.

## Family summary

| ID | Requirement family | Evidence | Status |
|---|---|---|---|
| V-01 | architecture and stable contracts | `tests/StorageChronicle.Architecture.Tests`, `StorageChronicle.slnx`, `docs/decisions` | verified |
| V-02 | source/normalization/state/storage | normalization, state, storage, and integration test projects | verified |
| V-03 | Windows filesystem/NTFS/session | platform test projects, confirmed runner tests, and `build/Test-WindowsPrivileged.ps1` | Notification/session boundaries and confirmed-runner implementation are tested; real NTFS/non-NTFS reconciliation and the full privileged capability matrix remain pending |
| V-04 | external media and mount history | `StorageChronicle.ExternalMedia.Tests` and filesystem tests | verified |
| V-05 | Event Stack and Diff View | projection, Event Stack, Diff View, and headless tests | verified |
| V-06 | IPC and bounded Agent pipeline | Agent tests, production named-pipe client/server test, and end-to-end smoke test | verified |
| V-07 | recovery, corruption, cancellation, capacity | Storage, State, Agent, and Normalization failure-path tests | verified |
| V-08 | documentation synchronization | `build/quality/Test-DocMirror.ps1` and DocMirrorValidator | verified |
| V-09 | packaging | WiX 6.0.2 project, manifest tests, `build/package/Build-Installer.ps1` | MSI build verified; physical install/update/rollback matrix pending |
| V-10 | performance acceptance | BenchmarkDotNet, `build/quality/Test-FullBenchmarkMatrix.ps1`, and exact private-working-set resource run | current bounded-batch portable matrix passed six non-MFT suites; the MFT 10K/100K/1M/candidate harness is expanded, but the dedicated volume and formal resource acceptance remain pending |

## Per-row verification of `Requirements/98_REQUIREMENTS_COVERAGE.md`

| ID | Coverage row | Verification evidence | Status |
|---|---|---|---|
| V-101 | Product name Storage Chronicle | installer manifest, project names, `Test-All` | verified |
| V-102 | Windows 10/11 x64, future Ubuntu, upstream Win10 support boundary | `docs/release/windows10-compatibility.md`, architecture tests, `Test-Windows10StageACapability.ps1` | boundary and fail-closed Windows 10 capability probe verified; Win10 Stage A/physical hardware run pending |
| V-103 | Avalonia; MVVM limited to UI | UI project references and architecture tests | verified |
| V-104 | UI, Service Agent, Session Agent separation | `src/StorageChronicle.Agent`, `src/StorageChronicle.SessionAgent`, UI projects, architecture tests | verified |
| V-105 | No MVP driver and future replacement seam | collector contracts, installer manifest test, architecture tests | verified |
| V-106 | NTFS USN and public-API MFT reconciliation | NTFS collector tests, confirmed runner tests, and privileged existing-journal test | USN/public-MFT boundary, initial pre-scan cursor/metadata route, candidate-only metadata, scoped backup privilege, and low-priority I/O implementation are verified by code/tests; real capability execution remains pending |
| V-107 | Non-NTFS notification monitoring and directory reconciliation | filesystem collector tests and confirmed runner tests | notification, initial-scan boundary, and diff-only Directory Reconciliation implementation are tested; real TestLab execution remains pending |
| V-108 | Non-NTFS initial-scan boundary and gap handling | `PolicyAndReconciliationTests`, `VolumeAndMediaTests` | verified |
| V-109 | Deterministic Source-to-Canonical normalization | `EventNormalizerTests`, normalization project | verified |
| V-110 | Settings persistence, UI, and settings history | settings tests and headless settings tests | verified |
| V-111 | No unsolicited USN creation or extension | NTFS API tests and privileged `QueriesExistingJournalWithoutCreatingOrResizing` | verified |
| V-112 | Non-NTFS best effort | filesystem and external-media failure tests | verified |
| V-113 | Ignore unreadable partitions | volume enumeration and collector failure tests | verified |
| V-114 | Drive-letter-free readable Volume GUID volumes | volume and snapshot tests | verified |
| V-115 | No file contents or content hashes | domain/storage contracts, architecture tests, source review, storage tests | verified |
| V-116 | Create, write, rename, move, delete, recycle, restore | normalization, state, projection, and golden fixture tests | verified |
| V-117 | Short-lived existence-only records | normalization and golden fixture tests | verified |
| V-118 | No synthetic descendant events for folder moves/deletes | state engine tests and architecture review | verified |
| V-119 | Historical path reconstruction through ancestor chains | state engine tests | verified |
| V-120 | Process Exact/Correlated/Unknown | domain, normalization, projection, and session tests | verified |
| V-121 | Deterministic Unknown grouping and parent/child process display | grouping/projection and UI headless tests | verified |
| V-122 | Explorer operation presentation | normalization and Session Agent tests | verified |
| V-123 | Clipboard candidates and repeated pastes | Session Agent and normalization tests | verified |
| V-124 | Relative folder-copy association and constrained cut handling | normalization tests | verified |
| V-125 | External-app copy remains create/write | normalization tests | verified |
| V-126 | Dedicated SMB share-state events | Session Agent, normalization, and projection tests | verified |
| V-127 | Local-only OneDrive state | filesystem/session contract tests | verified |
| V-128 | External media, PC primary history, and optional mirror | external-media tests and agent collector tests | verified |
| V-129 | Cross-PC import, Mount Session, and history branching | external-media manifest/import/recovery tests | verified |
| V-130 | Log exclusion and no system-area mirror | exclusion policy, media policy, and installer tests | verified |
| V-131 | Reconciliation only after detected gap; no manual command | health/reconciliation tests, confirmed runner tests, and IPC contract | prompt/decline contract and selected-volume Execute delegation are implemented/tested; real volume execution remains pending |
| V-132 | Defined reconciliation prompt and Yes/No decision | health panel, IPC, and headless tests | verified |
| V-133 | Source/Normalized/Grouped Event Stack | projection, Event Stack, and end-to-end tests | verified |
| V-134 | Group expansion, row process data, and paging | Event Stack tests and recursive IPC snapshots | verified |
| V-135 | Live/Period/Point/Replay Diff View | Diff View and headless tests | verified |
| V-136 | Live pause and Replay speed/event stepping | Diff View model tests | verified |
| V-137 | Move endpoints and OS Explorer opening | Diff View model tests | verified |
| V-138 | Fixed left Tree icon gutter | Diff View headless/contract tests | verified |
| V-139 | Eight Explorer modes, zoom, and no side tree | Diff View model and headless tests | verified |
| V-140 | Multiple split panes, ordering, and timeout | Diff View model tests | verified |
| V-141 | Colors, primary operation, and secondary icons | Diff projection and UI tests | verified |
| V-142 | Complete primary-operation priority | projection unit tests | verified |
| V-143 | Literal search, AND/OR/exclude, saved filters | projection, IPC, and settings tests | verified |
| V-144 | Append log, Zstandard, SQLite rebuild | storage tests, golden fixture, and integration tests | verified |
| V-145 | No history deletion feature | storage API review, architecture tests, installer retention test | verified |
| V-146 | Capacity stop and USN/reconciliation recovery | capacity, health, and lifecycle tests | verified |
| V-147 | 50 MiB and 0.5% background gate | `Test-ResourceBudget.ps1`, `Test-ResourceBudgetAcceptance.ps1`, `New-ResourceQuietWitness.ps1`, and `docs/release/performance-baseline.md` | wrapper enforces a 600-second dual-process run, fixed thresholds, lifecycle identity, queue gaps, and a schema-validated independent final-five-minute quiet witness; the formal run remains pending |
| V-148 | xUnit, Headless, ArchUnit, Benchmark | solution test projects, quality scripts, BenchmarkDotNet harness | frameworks/harness present; current bounded-batch BenchmarkDotNet matrix passed six non-MFT suites and 12/12 methods; configured MFT acceptance remains pending |
| V-149 | Mirrored source documentation | `Test-DocMirror.ps1` | verified |
| V-150 | main/devenv/feat and worktree separation | `TOP_CODEX.md`, `AGENTS.md`, `git worktree list`, handoffs | policy verified; feature branch is clean at the latest reviewed commit, while the required `devenv`/`main` integration sequence remains pending |
| V-151 | Staged top-agent foundation and delegated waves | branch/worktree history and handoff documents | delegation evidence exists; final top-agent merge sequence pending |
| V-152 | Ownership matrix and shared-contract synchronization | `Requirements/06_AGENT_OWNERSHIP_MATRIX.md`, handoffs, architecture tests | verified |
| V-153 | Normal installer, one product, retained data | WiX manifest test and successful MSI build | MSI build verified; physical install matrix pending |
| V-154 | Read-only I/O is not durable history | normalization tests, storage guard, architecture/source review | verified |

## Supplemental audit rows

| ID | Technical requirement | Verification evidence | Status |
|---|---|---|---|
| V-155 | C# language version uses the .NET 10 SDK default | `Directory.Build.props`, Release build | fixed in the current feature branch; `LangVersion=preview` was removed |
| V-156 | Native AOT compatibility publication configuration exists for Agent and Session Agent | `build/Test-AotCompatibility.ps1`, conditional common properties | configuration present; publication is opt-in and not an MVP acceptance gate |
| V-157 | Unsigned development and signed release procedures are separate without SmartScreen weakening | `docs/installer/signing-procedure.md` | documented; actual release signing remains an external release operation |
| V-158 | Scoped backup privilege and OS low-priority reconciliation I/O | `WindowsReconciliationInfrastructure`, `ConfirmedReconciliationRunner`, confirmed runner tests, and requirement 24 artifact gate | implemented with a dedicated reconciliation task, synchronous candidate-only thread scope, explicit privilege/ACL fallback and low-priority/I/O-hint/elapsed telemetry, and no event dropping; real privileged matrix evidence remains pending |
| V-159 | Agent diagnostic mode and Session Agent Clipboard-only IPC role | Agent entry point, named-pipe server identity, `NamedPipeServerTests`, and requirement 17 | verified in current feature branch; diagnostic mode is explicit, role/session hello is source-generated and validated, Session Agent is ClipboardCandidate-only, and an unpublished Session Agent process is rejected |

## Explicit measured or environment-bound items

The dedicated deterministic R-00 fixture now supplies fixed metadata-only evidence: process `Exact=1/3`, `Correlated=1/3`, `Unknown=1/3`, and Explorer source-correlation `1/3`. The pending process/Explorer items in the paragraph below refer to live Agent and interactive Session Agent capture, not this fixture result.

The repository contains a previous measured baseline for the background resource gate and the 10万-row Event Stack benchmark. The current portable matrix now supplies diagnostic evidence for all six non-MFT suites and 12/12 methods, and the current Release Agent/Session Agent has completed a 600-second diagnostic resource run; configured MFT capability and the formal resource acceptance run (non-diagnostic boundary plus independent final-five-minute quiet-period evidence) remain pending. Process-correlation rate, Explorer source-correlation rate, 2 TB-scale scan speed, physical Avalonia rendering at scale, Windows 10 22H2 execution, physical-media insertion, and clean install/update/rollback are environment acceptance measurements rather than hidden implementation stubs. The repository contains the required harnesses and records these as pending until the corresponding hardware/OS matrix is run; they must not be reported as Windows 10 or physical-install verified.

No row grants permission to weaken source quality, retain contents/hashes, delete history, or synthesize descendant events.

## Requirements 21–31 supplemental rows

| ID | Requirement | Evidence | Status |
|---|---|---|---|
| V-160 | Final acceptance orchestration and evidence order | `Requirements/21_FINAL_ACCEPTANCE_ORCHESTRATION.md`, `tools/TestEnvironment/Invoke-WindowsTestLab.ps1`, `tools/TestEnvironment/Compose-Windows10StageA.ps1` | harness and Stage A composition contract present; all environment groups not yet eligible |
| V-161 | Safe Hyper-V TestLab and disposable data VHDX | `tools/TestEnvironment/*.ps1`, read-only preflight | fail-closed scripts present; current Windows Home/no Hyper-V host is blocking |
| V-162 | Confirmed reconciliation execution | `ConfirmedReconciliationRunner`, IPC Execute path, bounded post-commit live-event buffer, 30 non-privileged Agent tests plus environment-gated NTFS/ACL-denied/non-NTFS acceptance tests | implementation and privileged test wiring verified; real NTFS/non-NTFS run pending |
| V-163 | NTFS candidate metadata, scoped privilege, low I/O | `WindowsReconciliationInfrastructure`, runner telemetry | implementation verified; elevated Windows execution pending |
| V-164 | Windows privileged capability matrix | `build/Test-Privileged.ps1`, `build/Test-WindowsPrivileged.ps1`, `build/quality/AcceptanceContracts.ps1` | NOT_EXECUTED on current host; the producer and final gates now require the fixed sixteen-capability Windows 11 x64/elevated contract |
| V-165 | Windows 10 22H2 compatibility | `Requirements/26_WINDOWS10_22H2_ACCEPTANCE.md`, `tools/TestEnvironment/Compose-Windows10StageA.ps1`, and TestLab definitions | NOT_EXECUTED; Stage A composer is fail-closed and no Stage A/Stage B run exists |
| V-166 | Formal idle resource gate | `build/quality/Test-ResourceBudgetAcceptance.ps1`, `build/quality/New-ResourceQuietWitness.ps1` | diagnostic evidence exists; the independent witness and strict schema gate are implemented, formal mode now rejects non-Windows 11 x64/known-VM environments, but the formal 600-second quiet-period artifact is pending |
| V-167 | MFT performance matrix | `MftBenchmarks`, `Test-FullBenchmarkMatrix.ps1` | real public-MFT enumeration/comparer methods and candidate-only production metadata/normalizer path emit the required per-run dataset/candidate/detail-query/generated-canonical/drop/time/allocation/environment oracle; the script and final gate now require Windows 11, all portable/MFT suites, and dedicated labeled-volume evidence; connected evidence and dedicated-volume run remain pending |
| V-168 | Physical installer acceptance | `build/package/New-ManualAcceptanceBundle.ps1`, `tools/PhysicalAcceptance/*`, `tools/TestEnvironment/Run-HyperVInstallerAcceptance.ps1`, installer build/test scripts and requirement 29 | physical install/repair/update/rollback not executed; explicit Hyper-V guest driver and host matrix orchestrator now exist and remain fail-closed, exact eleven case IDs/statuses are validated, while the Windows 11 VM prerequisite, VM/physical matrices still require approved Windows 11/Windows 10 targets, credentials, MSI inputs, a marked guest test root, and real execution evidence |
| V-169 | Real Agent/Explorer correlation | `build/quality/Test-CorrelationMetrics.ps1`, TestLab evidence contract | deterministic fixture only; `-LiveEvidencePath` is now a strict ingestion gate, but live correlation has not been executed |
| V-170 | Safe `devenv`/`main` integration | `Requirements/31_DEVENV_MAIN_INTEGRATION.md`, branch audit | pending all acceptance artifacts and remote branch/default-branch confirmation |
| V-171 | Final fail-closed acceptance aggregation | `build/quality/Test-FinalAcceptance.ps1`, `docs/build/quality/Test-FinalAcceptance.ps1.md`, `Compose-Windows10StageA.ps1`, `Invoke-Windows10StageACapabilityChecks.ps1` | verified as a schema-aware fail-closed gate, including the shared sixteen-capability contract, exact installer case IDs/statuses, required Windows 11 Hyper-V installer prerequisite, complete portable/MFT suite and dedicated-volume checks, physical resource identity, strict Stage A target/real-origin/evidence references, and independent capability producers; current invocation is blocked because all nine environment/integration artifacts are absent |
