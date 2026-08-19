# TestLab.Common.ps1

This helper is the shared safety boundary for the Hyper-V acceptance scripts. It loads only the user-approved ignored `TestLab.local.psd1`, validates the dedicated root and official local ISO paths, restricts VM names to `SC-Test-W11` and `SC-Test-W10`, and requires an elevated Windows host with Hyper-V/VMMS already available.

It never enables Windows features, reboots, changes firmware, downloads an ISO, touches a host physical volume, or deletes an unvalidated path. Either ISO may remain unset until its corresponding target is selected; callers validate only the selected guest ISO. Mutating callers must pass their own explicit apply/confirmation switch and must keep every VM disk under the approved TestLab root. The VM definitions enforce 2 vCPU, dynamic 2–6 GiB memory with a 4 GiB startup value, and approved VM names. Artifact output is restricted to `artifacts/acceptance/testlab/<run>`.

Relevant requirements: 21, 22, 25, 26, 28, 29, and 31. The callers are `Initialize-TestLab.ps1`, `Reset-TestVm.ps1`, `New-TestDataVhdx.ps1`, `Remove-TestDataVhdx.ps1`, `Invoke-TestLabCommand.ps1`, the copy helpers, and `Invoke-WindowsTestLab.ps1`.
