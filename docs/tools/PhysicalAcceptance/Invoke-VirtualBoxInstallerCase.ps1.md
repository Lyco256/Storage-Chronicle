# Invoke-VirtualBoxInstallerCase.ps1

Runs one real installer acceptance case inside the approved `SC-Test-W11-VBox` or `SC-Test-W10-VBox` guest through VirtualBox Guest Additions `VBoxManage guestcontrol`. It restores `SC-CLEAN-BASELINE` before each case, verifies the exact VM profile/disk/root/safety/exclusivity boundary, disconnects networking, waits for guest readiness, records a `whoami` smoke result, transfers only the case driver and MSI payloads, invokes `Invoke-RealInstallerCase.ps1` in the guest, and copies the result/evidence back to the host.

The host remains a normal non-administrator process. The guest credential is a user-created local-only host CLIXML reference; the non-admin credential reference is a separate path that must already exist inside the guest because host DPAPI credentials are not copied into the guest. No raw disk, host C:, shared folder, clipboard, drag-and-drop, USB, or network credential is used.

The driver starts with `Status=FAILED` and writes a result even when setup fails. It does not synthesize a pass from a process exit code. A pass requires the guest result to contain the matching target, isolated/elevated guest declaration, all passing assertions, and host-visible evidence. Missing Guest Additions, credential, VM, baseline, MSI, marker, or evidence remains failed/ineligible.
