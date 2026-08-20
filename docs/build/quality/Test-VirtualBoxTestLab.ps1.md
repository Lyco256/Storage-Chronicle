# Test-VirtualBoxTestLab.ps1

This is a provider-boundary contract test, not a fake acceptance run. It dot-sources `tools/TestEnvironment/VirtualBox.Common.ps1` and replaces only the `VBoxManage` process seam with deterministic machine-readable responses so the safety boundary can be tested without claiming a real guest. It verifies exact Windows 11 profile enforcement (4 GiB, 2 vCPU, EFI, TPM 2.0, dynamic VDI and 80 GiB logical size), missing safety properties, wrong memory, non-VDI disks, simultaneous VM rejection, and temporary guest password-file cleanup.

The real VirtualBox host/guest smoke, privileged matrix, installer matrix, MFT 10K/100K/1M runs, performance, and resource acceptance remain separate physical acceptance requirements and are never satisfied by this contract test.
