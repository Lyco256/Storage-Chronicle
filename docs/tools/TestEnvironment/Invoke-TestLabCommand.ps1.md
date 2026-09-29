# Invoke-TestLabCommand.ps1

Runs a supplied command or script through VirtualBox Guest Additions `VBoxManage guestcontrol` on exactly `SC-Test-W11-VBox` or `SC-Test-W10-VBox`. It requires the approved TestLab config, a real existing running VM, and a user-supplied local-only guest credential; it does not fabricate guest results or run against a host path. The host process remains non-administrator.
