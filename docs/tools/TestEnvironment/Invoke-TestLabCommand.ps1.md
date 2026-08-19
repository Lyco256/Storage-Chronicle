# Invoke-TestLabCommand.ps1

Runs a supplied command or script through PowerShell Direct on exactly `SC-Test-W11` or `SC-Test-W10`. It requires the approved TestLab config, elevated Hyper-V/VMMS availability, and a real existing VM; it does not fabricate guest results or run against a host path. Credentials are supplied by the user when the guest requires them.
