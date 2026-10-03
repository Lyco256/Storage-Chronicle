# Invoke-TestLabCommand.ps1

This legacy VirtualBox guest-command bridge is retired by Requirement 37. It does not read credentials, contact a guest, execute a command, or write evidence; it exits with an error for every invocation. Physical acceptance must not use an OS guest.

Runs a supplied command or script through VirtualBox Guest Additions `VBoxManage guestcontrol` on exactly `SC-Test-W11-VBox` or `SC-Test-W10-VBox`. It requires the approved TestLab config, a real existing running VM, and a user-supplied local-only guest credential; it does not fabricate guest results or run against a host path. The host process remains non-administrator.
