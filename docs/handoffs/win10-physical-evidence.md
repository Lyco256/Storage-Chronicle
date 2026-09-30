# Handoff: `feat/win10-physical-evidence`

## Scope

Reconcile the Windows 10 final-acceptance evidence path with Requirement 37. The previous finalizer required a VirtualBox Stage A manifest, despite the current acceptance policy requiring a physical Windows 10 22H2 host and retiring whole-OS guest execution.

## Changes

- `Finalize-Windows10PhysicalAcceptance.ps1` now consumes a Release `WindowsPrivilegedAcceptance.v2` manifest and requires all sixteen shared capabilities to pass on elevated Windows 10 22H2 x64, in addition to the physical preflight and all eleven physical installer cases.
- The privileged harness, installer harness, and Windows 10 preflight record `ComputerName`; both the composer and aggregate require all Windows 10 evidence inputs to originate from the same named PC.
- `Test-FinalAcceptance.ps1` independently validates the Windows 10 privileged evidence contract and no longer accepts Stage A/VM evidence for V-165.
- Updated the manual bundle instructions and mirrored documentation; the old Stage A composer remains legacy and is not used by this path.
- Added static contract assertions for the physical evidence path and explicit rejection of VM evidence.

## Validation

- `dotnet restore StorageChronicle.slnx --nologo` passed.
- `dotnet build StorageChronicle.slnx --no-restore --nologo` passed with 0 warnings and 0 errors.
- `build/Test-Fast.ps1 -NoRestore` passed all listed suites; Installer tests 11/11, Agent 51 passed/3 expected physical skips, Architecture 5/5, Windows filesystem 22/22, NTFS 23/23, Session 17/17, and all other listed unit/integration/UI suites passed. Initial invocation before the full solution build failed because a clean worktree lacked the Architecture test dependency assemblies; rerun after build passed.
- `build/Test-All.ps1` passed (exit code 0), including all default fast suites, quality/coverage gates, UI headless tests, and the mocked VirtualBox contract checks. No privileged/physical lane was invoked.
- PowerShell parser validation passed for all six modified scripts; `git diff --check` and `build/quality/Test-DocMirror.ps1` passed.
- Missing-evidence smoke checks for both the Windows 10 composer and final aggregator returned exit code 2 and `AcceptanceEligible=false`/`blocked` as required.
- No physical host, product, privileged runner, service, VHDX, or installer was started.

## Limitations

- The physical Windows 10 capability and installer runs remain `NOT_EXECUTED` and require Requirement 37's current-commit safety audit, approved isolation/evidence root, dedicated PC, and exact action/UAC confirmation.
- This change repairs evidence composition only; it does not certify runtime safety or Windows 10 compatibility.
