# Handoff: retire legacy VM acceptance entry points

Branch: `feat/retire-legacy-vm-paths`

Requirement basis: `TOP_CODEX.md`, Requirements 26 and 37; Requirements 22 and 34 explicitly mark their whole-OS VM procedures as superseded historical material.

## Changes

- Converted the old VirtualBox host preflight, Windows 10 guest capability/Stage A evidence, VM installer orchestration/driver, and artifact transfer entry points into parameter-compatible fail-closed stubs that return exit code 2 without host, VM, guest, installer, service, credential, file, or evidence I/O.
- Extended the VirtualBox contract test to parse each retired PowerShell entry point and permit only `Write-Error`, `exit`, and the fixed `$ErrorActionPreference = 'Stop'` assignment; dynamic method invocations and any additional commands or assignments fail the test.
- Updated installer source-contract tests so retired guest evidence cannot be treated as current acceptance evidence, while preserving the shared atomic/create-only writer checks for active physical evidence producers.
- Synchronized mirrored tool/build/test documentation and corrected the current migration inventory and Requirement 98 verification row to describe the retired state. No Requirements file was changed.

## Validation

- `pwsh -NoProfile -ExecutionPolicy Bypass -File build/quality/Test-VirtualBoxTestLab.ps1` — passed, 17 contract cases.
- `pwsh -NoProfile -ExecutionPolicy Bypass -File build/quality/Test-DocMirror.ps1` — passed after restoring the validator project assets; it also passed in the repository Quality gate.
- `pwsh -NoProfile -ExecutionPolicy Bypass -File build/Test-Fast.ps1` — passed; all 24 discovered non-privileged test projects passed with 0 build warnings/errors.
- PowerShell parser validation for the eight retired VM/Stage A scripts — passed without invoking the scripts.
- `pwsh -NoProfile -ExecutionPolicy Bypass -File build/quality/Test-Quality.ps1` — passed, including DocMirror, Architecture, Integration, 31 MFT physical seed contracts, coverage, and 17 VirtualBox retirement contracts.
- `git diff --check` — passed.

## Safety and remaining limits

- No VM, VHDX, installer, product service, host preflight, physical media, or privileged acceptance operation was run. Contract tests use mocked VirtualBox responses and temporary test-owned fixtures only.
- `VirtualBox.Common.ps1` remains as a mockable legacy helper library for existing non-privileged contract tests; no supported acceptance runner is intended to load it. Whole-OS guest acceptance remains retired.
- Requirement 37 remains `FAIL / DO_NOT_RUN`: full source-to-sink audit, approved isolation/evidence roots, read-only host preflight, independent process-attributed write monitor, dedicated-PC/UAC handoff, and physical acceptance are still required before any product or privileged execution.
- Windows 10 22H2 physical acceptance remains `NOT_EXECUTED` until a suitable physical test PC and user-approved bundle/handoff are available.
