# feat/benchmark-performance handoff

Updated 2026-08-20. This handoff records the top-agent changes on `feat/benchmark-performance`; it is a feature-branch checkpoint and not a release or `main` acceptance claim.

## Changes

- Added `build/package/New-ManualAcceptanceBundle.ps1` to create the Requirement 26 Windows 10 and Requirement 29 installer preparation bundles under ignored `artifacts/manual/`.
- Added real physical-target preflight, installer-case, result-collection, and explicit cleanup scripts under `tools/PhysicalAcceptance/`.
- The physical runner verifies the target ProductName/DisplayVersion/build, x64, administrator token, a dedicated TestLab data root containing `.storage-chronicle-testlab-marker.json`, free space, payload hashes, and an exact `YES` confirmation before invoking `msiexec`.
- Installer assertions inspect the actual MSI registration, default install/data paths, LocalSystem automatic service, exact 5,000/15,000/60,000 ms recovery delays, Session Agent logon registration/startup, non-admin launch/ACL denial, safe service stop before update, intentional failed-update rejection followed by a real rollback MSI, uninstall, and history retention. The runner refuses a dirty Program Files/ProgramData/product/service target. No history deletion behavior was added.
- Cleanup requires elevation, `-ConfirmCleanup`, and `ShouldProcess`; only explicitly marked VHDX files are dismounted/removed, and ProgramData history is never removed.
- Added `tools/TestEnvironment/Compose-Windows10StageA.ps1`, which composes the Windows 10 Stage A contract only from real TestLab Agent/Oracle evidence, the Windows 10 privileged matrix, the Hyper-V eleven-case installer artifact, and independent Cloud Files/no-driver checks. Missing or diagnostic inputs remain `NOT_EXECUTED`.
- Added `tools/PhysicalAcceptance/Finalize-Windows10PhysicalAcceptance.ps1` and corrected the physical bundle instructions to consume `results/windows10-preflight.json` from `Verify-Windows10PhysicalAcceptance.ps1`, not the installer's separate `RealMachineInstallerPreflight` artifact.
- Strengthened all final acceptance paths to require the defined eleven installer case IDs, not only a count of eleven passed rows. The privileged manifest now records ProductName, DisplayVersion, Build, and architecture for Windows 10 evidence binding. Added a regression contract test and mirrored documentation for every new source script; synchronized release readiness and requirements verification records.
- Closed a manual-bundle dependency gap: Windows 10 bundles now carry the shared `AcceptanceContracts.ps1` file and hash it in both bundle manifests, while `Finalize-Windows10PhysicalAcceptance.ps1` resolves the repository or bundle-local contract. Added a static regression test and verified the copied finalizer produces a fail-closed `NOT_EXECUTED` artifact when inputs are absent.

## Validation commands and results

- PowerShell parser validation for `build/package/New-ManualAcceptanceBundle.ps1` and all six `tools/PhysicalAcceptance/*.ps1`: passed.
- Strict bundle generation without updated/rollback MSI inputs: exited 1 and created no output bundle.
- Preparation bundle generation with `-AllowIncompleteBundle -Force`: exited 0; manifest recorded missing updated/rollback MSI inputs and `AcceptanceEligible=false`.
- `Verify-Windows10PhysicalAcceptance.ps1` on the current Windows 11 Home 25H2 non-admin host: exited 2; recorded ProductName, DisplayVersion, administrator, and marker failures with `AcceptanceEligible=false`.
- `Run-RealMachineInstallerAcceptance.ps1` on the same host: exited 2 with `StorageChronicle.RealMachineInstallerFailure.v1` / `WRONG_ENVIRONMENT`; it did not invoke the installer matrix.
- `Collect-PhysicalAcceptanceResults.ps1` on the preparation bundle: exited 0 and retained `AcceptanceEligible=false`.
- `git diff --check`: passed.
- `Compose-Windows10StageA.ps1` with no inputs: exited 2 and wrote `StorageChronicle.Windows10StageAAcceptance.v1` with `AcceptanceEligible=false`.
- `Finalize-Windows10PhysicalAcceptance.ps1` with no inputs: exited 2 and wrote `StorageChronicle.Windows10PhysicalAcceptance.v1` with `AcceptanceEligible=false`.
- PowerShell parser over `build/` and `tools/`: 0 errors; `Test-DocMirror.ps1`: passed.
- `build/Test-Fast.ps1 -NoRestore`: exit code 0; 22 non-privileged projects passed. Agent: 30 passed, 3 expected environment-gated skips. Installer contract tests: 3 passed.
- `dotnet build StorageChronicle.slnx --no-restore --nologo -v:minimal`: 0 warnings, 0 errors.
- `build/quality/Test-FinalAcceptance.ps1` with no artifacts: exit code 2; all nine blocking groups remained `NOT_EXECUTED` and `AcceptanceEligible=false`.
- Preparation bundle contract-path check: Windows 10 bundle generation completed; `AcceptanceContracts.ps1` existed, appeared once in `hash-manifest.json`, and appeared in `bundle-manifest.json` payloads. Running the copied finalizer with no inputs exited 2 and wrote `StorageChronicle.Windows10PhysicalAcceptance.v1` with `Status=NOT_EXECUTED`.
- `dotnet build StorageChronicle.slnx --no-restore --nologo -v:q /nodeReuse:false`: 0 warnings, 0 errors; installer contract tests: 7 passed. `build/Test-Fast.ps1 -NoRestore`: exit code 0; 22 project logs completed with 0 failures, 3 known environment-gated skips. PowerShell parser: 48 files, 0 errors; `Test-DocMirror.ps1`: passed.

The repository-wide Build, Fast, quality, UI, and coverage validation passed on 2026-08-20 via `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/Test-All.ps1` with exit code 0. This remains non-privileged validation; the script intentionally isolates the privileged Windows acceptance lane, which remains pending.

## Known limitations and required exit evidence

- The current host is Windows 11 Home without Hyper-V/VMMS, a supported Windows 10 22H2 target, administrator elevation, or a physical acceptance target. No physical or privileged acceptance is claimed.
- Updated and rollback MSI inputs are not present, so the generated bundle is intentionally a preparation bundle only.
- Requirements 21–31 still require eligible real artifacts for the privileged capability matrix, Windows 10 22H2, formal resource quiet-period, labeled MFT matrix, physical installer, live Agent/Explorer correlation, and final branch integration gates.
- The Agent pipeline/reconciliation boundary was hardened after this handoff: post-commit ordinary source events are captured in a bounded volume-scoped session and replayed after a scan; overflow creates a failed gap. The new privileged manifest is v2 and refuses USN journal mutation. These changes are implementation hardening, not real-environment acceptance evidence.
- The metadata-only workload now emits v2 process evidence, per-operation UTC start/end fields, and supports `full`, `burst`, `parallel`, `short-lived`, `acl-denied`, and distributed zero-byte `mft` scenarios. `Invoke-WindowsTestLab.ps1` selects the full scenario for Workload/NonNtfs, the ACL-denied scenario for AclDenied, and the MFT scenario for Mft. A real Agent/Oracle/Source/Canonical/State comparison path is wired through `StorageChronicle.RealIoOracleValidator`; it remains environment-gated and has not run on the current host.
- The privileged runner now builds both the platform integration tests and Agent tests, dispatching reconciliation, ACL-denied metadata, and non-NTFS traits to production Agent tests. The latest targeted validation records 30 non-privileged Agent passes and three expected environment-gated skips; this is not a privileged acceptance pass.
- The MFT gate now requires the Requirement 28 per-run correctness oracle (dataset/enumerated/candidate/detail-query/generated-canonical/dropped counts plus environment), and `WindowsMftBenchmarks` now emits that artifact from its real public-API enumeration/comparer methods. The dedicated labeled-volume/TestLab run remains blocking; the live correlation wrapper still has no real Agent/Explorer capture of its own. Neither is represented as a pass.
- The MFT candidate-small method now executes the production Windows metadata reader, scoped `SeBackupPrivilege`/low-I/O scopes, and production normalizer for each actual candidate instead of emitting zero-detail-query placeholder counters. This is implementation coverage, not a durable TestLab MFT acceptance artifact. The formal resource gate now has a separate `New-ResourceQuietWitness.ps1` producer and rejects legacy hand-written quiet JSON unless it is a passed witness schema artifact.
- The no-input gate audit corrected three harness contracts: localized WMI x64 output is evaluated through the host OS API, and the privileged, MFT matrix, and installer planning paths now emit `NOT_EXECUTED` evidence with exit code 2 when their user-approved environments are absent. No acceptance status is promoted by these changes.
- The Windows 10 Stage A composer deliberately requires independent `StorageChronicle.Windows10StageACheck.v1` artifacts for Cloud Files capability and no-driver confirmation; the producers now exist but have not run on this host, so those checks remain an explicit environment-bound item rather than a source-code-only claim.
- Added `Test-Windows10StageACapability.ps1` and `Invoke-Windows10StageACapabilityChecks.ps1`. The guest probe performs real `cldapi.dll` export detection and fail-closed no-driver metadata checks; the host runner targets only a running approved `SC-Test-W10` and copies the guest artifacts without synthesizing results. The Stage A physical finalizer and final acceptance gate now reject duplicate/extra checks, non-real origins, missing evidence paths, and wrong VM identity. These producers have not run on this host and therefore do not change acceptance status.
- Reconciliation failure handling now records a failed gap even when the selected volume disappears before descriptor resolution, and catches unexpected execution exceptions without returning completed success. A regression test covers the detached-volume path.
- `devenv` and `main` must remain unmerged until all required artifacts are eligible and the ordered `--no-ff` integration gates pass.
