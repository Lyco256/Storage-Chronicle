# Test-WindowsPrivileged.ps1

Runs the Windows-targeted privileged acceptance wrapper separately from the fast suite. It forwards the approved `TestLabRoot`, explicit VHDX/device/media/SMB/service/session inputs, real workload oracle, Agent history directory, and fail-closed journal options to `Test-Privileged.ps1`. Privileged hardware acceptance remains an explicit host concern; missing capabilities or product evidence produce `NOT_EXECUTED` and `AcceptanceEligible=false` rather than a synthetic pass.
