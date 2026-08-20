# VirtualBox migration inventory

This inventory records the migration audit required by Requirements 32 and 36. The initial audit was performed after `git fetch --all --prune` on branch `feat/testlab-virtualbox-migration`, starting at commit `dbf167f`. The repository search excluded `.git`, `artifacts`, `bin`, and `obj` and covered `Hyper-V`, `Microsoft-Hyper-V`, `New-VM`, `Set-VM`, `Start-VM`, `Stop-VM`, `Checkpoint-VM`, `Restore-VMSnapshot`, `New-PSSession -VMName`, `Copy-VMFile`, `New-VHD`, `Mount-VHD`, `Dismount-VHD`, `vmms`, `Hyper-V Administrators`, `PowerShell Direct`, and `TestLab`.

## Classification

| Classification | Paths and rationale |
| --- | --- |
| KEEP | `build/quality/AcceptanceContracts.ps1`, `build/quality/Test-FullBenchmarkMatrix.ps1`, `build/quality/Test-ResourceBudgetAcceptance.ps1`, `build/quality/New-ResourceQuietWitness.ps1`, `tools/StorageChronicle.FileMutationWorkload`, `tools/StorageChronicle.RealIoOracleValidator`, `tools/StorageChronicle.ResourceMonitor`, `tools/StorageChronicle.TestDataGenerator`, `src/StorageChronicle.Agent`, `src/StorageChronicle.Platform.Windows.*`, and the existing Windows integration tests. These contain product behavior, safety markers, real-I/O oracle rules, MFT/USN/ETW/Service/SMB/correlation logic, or physical resource policy rather than VM-provider control. |
| REWRITE | `tools/TestEnvironment/TestLab.Common.ps1`, `VirtualBox.Common.ps1`, `Test-TestLabPrerequisites.ps1`, `Initialize-TestLab.ps1`, `Reset-TestVm.ps1`, `Invoke-TestLabCommand.ps1`, `Copy-TestArtifactsToVm.ps1`, `Copy-TestResultsFromVm.ps1`, `New-TestDataVhdx.ps1`, `Remove-TestDataVhdx.ps1`, `Invoke-WindowsTestLab.ps1`, `Invoke-Windows10StageACapabilityChecks.ps1`, `Compose-Windows10StageA.ps1`, `build/Test-Privileged.ps1`, `build/package/Test-Installer.ps1`, `build/quality/Test-FinalAcceptance.ps1`, `tools/PhysicalAcceptance/Finalize-Windows10PhysicalAcceptance.ps1`, and the platform hot-attach acceptance test. Their safety and evidence contracts remain, while VM creation, start/stop, snapshot restore, guestcontrol transfer, and disposable-disk attachment now use VirtualBox or Windows Storage APIs. |
| REMOVE | `tools/TestEnvironment/Run-HyperVInstallerAcceptance.ps1` and `tools/PhysicalAcceptance/Invoke-HyperVInstallerCase.ps1`. These were provider-specific runtime entry points and were replaced by `Run-VirtualBoxInstallerAcceptance.ps1` and `Invoke-VirtualBoxInstallerCase.ps1`. The old names are not retained as compatibility shims so an obsolete caller cannot silently select Hyper-V. |
| HISTORICAL_DOC | `Requirements/22_SAFE_HYPERV_TESTLAB.md` and the old Hyper-V-specific documentation under `docs/tools` remain only as historical context. Requirement 22 now begins with a supersession notice pointing to Requirements 32 and 33. Historical acceptance artifacts and Git history are not deleted. |

## Current provider boundary

The only provider control boundary is `tools/TestEnvironment/VirtualBox.Common.ps1`. It owns VBoxManage discovery, process execution, VM identity and disk-root validation, 4 GiB/2-vCPU settings, baseline restore, guestcontrol, host-to-guest/result transfer, resource gates, and safe-setting checks. Product assemblies do not reference VBoxManage. `build/Test-Privileged.ps1` runs inside the Windows guest and creates its disposable VHDX with DiskPart plus Windows Storage cmdlets; it does not require the Hyper-V PowerShell module.

The required VirtualBox identities are `SC-Test-W11-VBox` and `SC-Test-W10-VBox`. The guest OS disks are dynamic VDI files under the approved TestLabRoot. The guest-internal acceptance VHDX remains the destructive test boundary and retains the existing marker schema, TestRun, role, label, and filesystem checks.

## Remaining evidence

Static migration evidence and fail-closed paths are implemented on this branch. Real migration smoke, guest Additions/guestcontrol, Windows privileged matrix, MFT/USN/ETW/Service/SMB/correlation, installer, Windows 10 Stage A, and resource measurements remain unexecuted until the user completes the Requirements 34 VirtualBox installation, ISO, firmware, guest setup, credential, and baseline-snapshot handoff. No `NOT_EXECUTED` or host-preflight result is treated as an acceptance pass.
