# Handoff: read-only Windows 10 host preflight

Branch: `feat/readonly-host-preflight`

## Changes

- Replaced the Windows 10 physical preflight with a read-only, fail-closed inventory. It validates local non-reparse paths, separates test and evidence roots from protected locations, requires a dedicated non-system fixed NTFS volume with safe system/boot/USB role checks and 40 GiB free, binds the two Workload markers to the actual volume identity, and refuses marker reparse points before opening them.
- Checks for an existing Storage Chronicle installation/history, Agent service name collision, and acceptance SMB share collision. Inventory errors are recorded as failed checks.
- Writes a unique report directly under a pre-existing approved EvidenceRoot with `FileMode.CreateNew`; it always declares `AcceptanceEligible = false`.
- Updated the Windows 10 bundle instructions, finalizer documentation, installer static contract test, and V-165 verification note.

## Validation

- PowerShell parser validation for the verifier, finalizer, and bundle generator: passed.
- `git diff --check`: passed (Git reports its standard LF-to-CRLF normalization warning for the new PowerShell script).
- `build/Test-All.ps1`: passed; all projects built with zero warnings/errors, all enabled tests passed, and three elevated acceptance tests were skipped because they require a dedicated elevated NTFS/non-NTFS host.
- `build/quality/Test-DocMirror.ps1`: passed.
- Installer static test for the fail-closed verifier contract: passed in `Test-All`.

## Safety and limitations

- No product, service, installer, VHDX, physical workload, or privileged acceptance command was run.
- This preflight does not establish that the host is physically non-virtual (the model check is heuristic), does not attest the static source-to-sink audit, and does not provide independent runtime write monitoring. Requirement 37 physical acceptance therefore remains `NOT_EXECUTED`.
- The host preflight has not been run on a dedicated user-confirmed Windows 10 22H2 machine or approved test/evidence volumes.
