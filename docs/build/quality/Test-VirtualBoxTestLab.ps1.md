# Test-VirtualBoxTestLab.ps1

This non-privileged contract test uses fake VirtualBox command responses and a run-owned temporary fixture; it never launches VBoxManage or creates a VM. It parses every legacy VM creation, guest-control, transfer, snapshot-reset, Windows 10 guest Stage A, and guest installer-acceptance entry point and permits only the fail-closed `Write-Error`/`exit` commands plus a fixed error-preference assignment. Dynamic method invocations and other commands/assignments fail the contract. The temporary fixture is removed only after its exact run GUID, expected parent, and marker are verified.

The remaining provider-library contract cases validate exact VM profile and disk rules through a mocked command seam. They are isolated source-contract tests only, not acceptance evidence, and do not authorize whole-OS VM construction. Physical acceptance and lightweight file-backed VHDX tests remain gated by Requirement 37.
