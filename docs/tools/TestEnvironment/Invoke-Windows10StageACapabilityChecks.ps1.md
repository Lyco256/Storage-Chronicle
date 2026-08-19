# Invoke-Windows10StageACapabilityChecks.ps1

## Role

Host-side orchestrator for the Windows 10 Stage A Cloud Files and no-driver probes. It targets only the approved `SC-Test-W10` Hyper-V guest and copies the guest's independently generated evidence back under `artifacts/acceptance/testlab`.

## Inputs and execution boundary

The command requires an approved local TestLab configuration and Windows 10 22H2 ISO. Without `-Apply` it performs preflight only and writes `NOT_EXECUTED`. With `-Apply`, it requires Hyper-V, VMMS, administrator rights, an existing running `SC-Test-W10`, and PowerShell Direct; it does not enable Hyper-V, create a VM, or start a stopped VM.

## Failure behavior and invariants

The host rewrites only the copied evidence paths to host-visible paths. It does not synthesize a check or promote a guest failure. Missing guest artifacts, a nonzero guest result, wrong VM identity, or an unavailable prerequisite remains ineligible. The resulting check files are consumed by `Compose-Windows10StageA.ps1`.

## Dependencies and tests

Dependencies are `TestLab.Common.ps1`, an approved TestLab root, the `SC-Test-W10` VM, and guest credentials when required by PowerShell Direct. Tests cover parser/doc-mirror/static contract and the no-`-Apply` fail-closed path; real execution requires the user-approved Windows 10 TestLab.
