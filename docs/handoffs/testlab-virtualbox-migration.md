# TestLab VirtualBox migration handoff

## Scope

This handoff covers the code-side migration on `feat/testlab-virtualbox-migration`. The authoritative migration requirements are `Requirements/32_VIRTUALBOX_MIGRATION_AND_SUPERSESSION.md` through `Requirements/36_VIRTUALBOX_MIGRATION_ACCEPTANCE_AND_REAUDIT.md`; `Requirements/22_SAFE_HYPERV_TESTLAB.md` contains the required supersession notice.

## Changes

- Replaced Hyper-V TestLab control with the platform-neutral TestLab entry points backed by `VBoxManage` and VirtualBox Guest Additions guest control.
- Added exact Windows 11 and Windows 10 VM definitions, safe-device checks, dynamic VDI provisioning, baseline restore, one-VM-at-a-time execution, bounded guest copy/command helpers, and fail-closed cleanup.
- Replaced host-side Hyper-V VHD control in `build/Test-Privileged.ps1` with DiskPart and Windows Storage cmdlets while retaining the guest-internal disposable VHDX contract and markers.
- Migrated installer acceptance, Windows 10 Stage A, final acceptance, correlation, packaging, and documentation references.
- Removed the obsolete Hyper-V installer orchestrator and driver; historical documentation remains explicitly marked as superseded.
- Added mirrored documentation for every new or changed source/tool file and recorded the migration inventory and review.

## Validation commands and results

- PowerShell parser scan for repository scripts, excluding generated/build output: 51 files, 0 errors.
- `git diff --check`: passed.
- `build/quality/Test-DocMirror.ps1`: exit 0.
- `build/quality/Test-VirtualBoxTestLab.ps1`: 6/6 provider-boundary contract cases passed; this does not claim a real guest run.
- `build/quality/Test-Quality.ps1`: exit 0; architecture, integration, coverage, and VirtualBox boundary contract checks passed.
- `build/Test-Fast.ps1 -NoRestore`: exit 0; all runnable suites passed, with only the existing environment-gated skips.
- Release Installer tests: 8/8 passed.
- Release LiveCorrelation validator tests: 4/4 passed.
- Release Windows Integration build: 0 errors.
- Forbidden runtime Hyper-V-control scan: no matches in source/build/tools/tests; historical docs and audit-only references are intentionally retained.
- Host preflight negative-path run: exit 2 as required when the host is not ready; no VM mutation was attempted.

## Known limitations and required user handoff

Real VirtualBox execution has not been claimed. The latest normal-user preflight observed no discoverable `VBoxManage.exe`, no supplied official ISO paths, and approximately 2.67 GiB available host memory, below the 6 GiB gate. No VM, Guest Additions installation, baseline snapshot, guest credential, installer matrix, Stage A, 10K/100K/1M MFT acceptance, performance acceptance, or final physical re-audit was executed.

The user must complete the fixed handoff in Requirements 34: install the approved VirtualBox 7.2.x Windows host package through normal UAC with no Extension Pack, provide official ISO media and a safe local NTFS TestLabRoot, resolve any real firmware/virtualization issue without disabling security controls, complete guest setup and matching Guest Additions, create the exact `SC-CLEAN-BASELINE`, and send back only non-secret preflight fields. Host applications must be closed until the latest 2.67 GiB available-RAM reading reaches the 6 GiB gate. Codex must remain a normal non-administrator host process.

## Merge gate

This branch is not ready to merge to `devenv` or `main` until the user-controlled handoff is complete and the real VirtualBox acceptance sequence, installer cases, Stage A, performance/resource tests, correlation evidence, cleanup/recovery, and Requirements 36 final re-audit all pass.
