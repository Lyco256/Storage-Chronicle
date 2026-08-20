# Historical / superseded: Run-HyperVInstallerAcceptance.ps1

This document describes the removed Hyper-V provider orchestrator and is retained only for historical traceability. Current runtime entry point: `Run-VirtualBoxInstallerAcceptance.ps1`, governed by Requirements 32–36. Do not use this document as an execution guide.

Restores one approved TestLab VM to `SC-CLEAN-BASELINE`, starts it, invokes the generic eleven-case installer harness with the real `Invoke-HyperVInstallerCase.ps1` guest driver, validates that exactly one new eligible generic installer manifest with the defined eleven case IDs was produced for the selected Hyper-V target, records that evidence path in the matrix manifest, and stops the VM in `finally`. The final acceptance gate consumes the Windows 11 manifest as a required prerequisite for the Windows 11 physical matrix. Without `-Apply` it performs only preflight and writes a non-eligible plan. It requires user-provided MSI versions, a DPAPI-protected guest credential, guest non-admin credential reference, approved ISO/TestLab configuration, and does not enable Hyper-V or create a VM automatically.

## Role

Top-level Windows 10/11 Hyper-V installer acceptance orchestration.

## Invariants

Only `SC-Test-W11`/`SC-Test-W10` and disks under the approved TestLab root are accepted. A successful harness result is still tagged `HyperVVm`/`VM`; it cannot satisfy the physical-machine final installer gate.

## Failure behavior

Missing inputs, wrong VM identity/state, absent baseline, guest failure, any `NOT_EXECUTED`, or cleanup failure produces `AcceptanceEligible=false` and a nonzero result.

## Tests and validation

PowerShell parser and fail-closed preflight validation are host-testable. Actual MSI matrix execution is pending an approved Hyper-V Windows 10/11 TestLab.
