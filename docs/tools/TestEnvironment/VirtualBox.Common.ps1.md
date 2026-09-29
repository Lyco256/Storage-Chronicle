# VirtualBox.Common.ps1

This is the provider-specific control boundary for the VirtualBox TestLab. It discovers and invokes the approved 7.2.x `VBoxManage.exe`, restricts VM identities and disk paths, enforces the exact 4096 MiB/2-vCPU/EFI/TPM/OS-type/dynamic-VDI/logical-size profile, rejects simultaneous TestLab VMs and missing safety properties, controls provisioning NAT versus acceptance-time offline state, restores `SC-CLEAN-BASELINE`, and transfers guest commands and artifacts through Guest Additions `guestcontrol`.

The helper never elevates the host, installs VirtualBox, changes firmware, enables Hyper-V, maps raw disks, exposes host C:, creates shared folders, or stores a persistent guest password. Transient password files are created only below the approved TestLab root and removed in `finally`. OS disks and host disposable data disks must remain under the approved root. The destructive acceptance VHDX is created inside the Windows guest by `build/Test-Privileged.ps1`; this helper only owns the VirtualBox-side VDI attachment needed to provide that guest volume.

Public functions include `Get-TestLabConfig`, `Assert-TestLabRoot`, `Get-TestLabVmDefinition`, `Assert-ExactTestLabVm`, `Assert-TestLabVmDisks`, `Assert-TestLabVmProfile`, `Assert-TestLabVmExclusive`, `Assert-VirtualBoxResourceGate`, `Assert-VBoxSafeSettings`, `Set-VBoxVmProvisioningSettings`, `Start-TestLabVm`, `Stop-TestLabVm`, `Restore-TestLabBaseline`, `Ensure-TestLabBaseline`, `Invoke-VBoxGuestControl`, `Wait-TestLabGuestReady`, `Copy-TestArtifactToVm`, and `Copy-TestArtifactFromVm`.

The provider contract test in `build/quality/Test-VirtualBoxTestLab.ps1` exercises these failure boundaries without claiming real guest acceptance.

Failure behavior is fail-closed: missing VBoxManage, unsupported version, missing VM/snapshot, unsafe disk/root, disabled safety setting, guest-control failure, or resource threshold failure throws before acceptance evidence is written. Relevant requirements are 32–36 and the shared TestLab requirements 21–31.
