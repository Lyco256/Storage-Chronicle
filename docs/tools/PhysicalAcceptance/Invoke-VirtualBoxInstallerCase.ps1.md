# Invoke-VirtualBoxInstallerCase.ps1

This compatibility entry point is retired under Requirements 26 and 37. It fails closed before loading the old TestLab helper and performs no host, VM, guest, installer, service, file, credential, or evidence I/O. Whole-OS guest installer tests are not part of the current physical acceptance plan.

Use the separately reviewed physical installer workflow only after Requirement 37's static audit, dedicated-PC checks, exact action disclosure, and per-case UAC boundary are satisfied. This retired entry point cannot produce acceptance evidence.
