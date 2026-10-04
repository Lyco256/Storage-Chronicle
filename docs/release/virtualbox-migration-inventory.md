# VirtualBox migration inventory

This inventory records the migration audit required by Requirements 32 and 36. The initial audit was performed after `git fetch --all --prune` on branch `feat/testlab-virtualbox-migration`, starting at commit `dbf167f`. The repository search excluded `.git`, `artifacts`, `bin`, and `obj` and covered `Hyper-V`, `Microsoft-Hyper-V`, `New-VM`, `Set-VM`, `Start-VM`, `Stop-VM`, `Checkpoint-VM`, `Restore-VMSnapshot`, `New-PSSession -VMName`, `Copy-VMFile`, `New-VHD`, `Mount-VHD`, `Dismount-VHD`, `vmms`, `Hyper-V Administrators`, `PowerShell Direct`, and `TestLab`.

Historical-status note (2026-10-04): this inventory describes the superseded whole-OS VirtualBox migration, not an authorized current test procedure. Requirement 37 disallows whole-OS guests; VM creation/control, snapshot, transfer, host preflight, guest capability, Stage A composition, and guest installer entry points are no-I/O retired stubs. `VirtualBox.Common.ps1` remains only as a mockable legacy contract library and has no supported acceptance runner. Lightweight, run-owned file-backed VHDX testing is a separate physical-machine workflow and remains forbidden until all Requirement 37 gates pass.

## Classification

| Classification | Paths and rationale |
| --- | --- |
| KEEP | `build/quality/AcceptanceContracts.ps1`, `build/quality/Test-FullBenchmarkMatrix.ps1`, `build/quality/Test-ResourceBudgetAcceptance.ps1`, `build/quality/New-ResourceQuietWitness.ps1`, `tools/StorageChronicle.FileMutationWorkload`, `tools/StorageChronicle.RealIoOracleValidator`, `tools/StorageChronicle.ResourceMonitor`, `tools/StorageChronicle.TestDataGenerator`, `src/StorageChronicle.Agent`, `src/StorageChronicle.Platform.Windows.*`, and the existing Windows integration tests. These contain product behavior, safety markers, real-I/O oracle rules, MFT/USN/ETW/Service/SMB/correlation logic, or physical resource policy rather than VM-provider control. |
| RETIRED | `tools/TestEnvironment/TestLab.Common.ps1`, `VirtualBox.Common.ps1`, `Test-TestLabPrerequisites.ps1`, `Initialize-TestLab.ps1`, `Reset-TestVm.ps1`, `Invoke-TestLabCommand.ps1`, `Copy-TestArtifactsToVm.ps1`, `Copy-TestResultsFromVm.ps1`, `New-TestDataVhdx.ps1`, `Remove-TestDataVhdx.ps1`, `Invoke-WindowsTestLab.ps1`, `Invoke-Windows10StageACapabilityChecks.ps1`, `Test-Windows10StageACapability.ps1`, `Compose-Windows10StageA.ps1`, `Run-VirtualBoxInstallerAcceptance.ps1`, and `tools/PhysicalAcceptance/Invoke-VirtualBoxInstallerCase.ps1`. VM- and guest-derived acceptance is prohibited by Requirements 26/37; executable entry points fail closed without I/O. Physical workflows in `build/Test-Privileged.ps1`, `build/package/Test-Installer.ps1`, `build/quality/Test-FinalAcceptance.ps1`, and `tools/PhysicalAcceptance/*` are separately guarded by Requirement 37. |
| REMOVE | `tools/TestEnvironment/Run-HyperVInstallerAcceptance.ps1` and `tools/PhysicalAcceptance/Invoke-HyperVInstallerCase.ps1`. These were provider-specific runtime entry points and were replaced by `Run-VirtualBoxInstallerAcceptance.ps1` and `Invoke-VirtualBoxInstallerCase.ps1`. The old names are not retained as compatibility shims so an obsolete caller cannot silently select Hyper-V. |
| HISTORICAL_DOC | `Requirements/22_SAFE_HYPERV_TESTLAB.md` and the old Hyper-V-specific documentation under `docs/tools` remain only as historical context. Requirement 22 now begins with a supersession notice pointing to Requirements 32 and 33. Historical acceptance artifacts and Git history are not deleted. |

## Current provider boundary

There is no supported provider control boundary for acceptance. `VirtualBox.Common.ps1` is retained solely for non-privileged mocked contract coverage; no supported runner imports it. Product assemblies do not reference VBoxManage. Physical privileged tests and lightweight VHDX fixtures are governed exclusively by Requirement 37 gates.

The required VirtualBox identities are `SC-Test-W11-VBox` and `SC-Test-W10-VBox`. The guest OS disks are dynamic VDI files under the approved TestLabRoot. The guest-internal acceptance VHDX remains the destructive test boundary and retains the existing marker schema, TestRun, role, label, and filesystem checks.

## Remaining evidence

Whole-OS migration smoke, Guest Additions/guestcontrol, and guest-derived Windows 10 Stage A are retired and will not be executed. Windows privileged matrix, MFT/USN/ETW/Service/SMB/correlation, installer, Windows 10 physical compatibility, and resource measurements remain unexecuted pending Requirement 37 static/source-to-sink audit, approved isolation roots, normal-user preflight, independent runtime write monitoring, and the required dedicated physical test hardware. No `NOT_EXECUTED` result is treated as an acceptance pass.
