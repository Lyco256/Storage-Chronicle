# Run-HyperVInstallerAcceptance.ps1

Restores one approved TestLab VM to `SC-CLEAN-BASELINE`, starts it, invokes the generic eleven-case installer harness with the real `Invoke-HyperVInstallerCase.ps1` guest driver, records the matrix manifest, and stops the VM in `finally`. Without `-Apply` it performs only preflight and writes a non-eligible plan. It requires user-provided MSI versions, a DPAPI-protected guest credential, guest non-admin credential reference, approved ISO/TestLab configuration, and does not enable Hyper-V or create a VM automatically.

## Role

Top-level Windows 10/11 Hyper-V installer acceptance orchestration.

## Invariants

Only `SC-Test-W11`/`SC-Test-W10` and disks under the approved TestLab root are accepted. A successful harness result is still tagged `HyperVVm`/`VM`; it cannot satisfy the physical-machine final installer gate.

## Failure behavior

Missing inputs, wrong VM identity/state, absent baseline, guest failure, any `NOT_EXECUTED`, or cleanup failure produces `AcceptanceEligible=false` and a nonzero result.

## Tests and validation

PowerShell parser and fail-closed preflight validation are host-testable. Actual MSI matrix execution is pending an approved Hyper-V Windows 10/11 TestLab.
