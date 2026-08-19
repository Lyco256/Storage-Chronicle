# Invoke-WindowsTestLab.ps1

Orchestrates the safe Windows 11/Windows 10 TestLab stages after the user has approved the local config and passed `-Apply`: baseline reset, a new disposable workload VHDX, a real guest mutation workload, and artifact logging. It invokes the other TestEnvironment scripts and is intentionally fail-closed when the baseline, guest workload executable, PowerShell Direct session, or artifact is missing.

The script records Explorer correlation as `NOT_EXECUTED` unless an independent human-assisted evidence file is supplied; it never synthesizes Explorer events or marks the final acceptance eligible. It does not install Hyper-V, reboot, download ISOs, touch physical host media, or treat a partial Windows 10/Windows 11 run as final acceptance.
