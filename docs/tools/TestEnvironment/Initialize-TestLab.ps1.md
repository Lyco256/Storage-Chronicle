# Initialize-TestLab.ps1

Validates the user-approved TestLab config in read-only mode by default. `-Target Windows11` requires only the Windows 11 ISO; `-Target Windows10` requires only the Windows 10 22H2 ISO; `-Target Both` requires both. With explicit `-Apply`, it creates or validates only the selected `SC-Test-W11`/`SC-Test-W10` Generation 2 VMs under the approved root, with two vCPUs, dynamic 2–6 GiB memory and a 4 GiB startup value, 80/64 GiB OS VHDX, Windows 11 Secure Boot/vTPM, and no network adapter. It verifies every existing VM disk remains under the approved root and attaches only the supplied local official ISO paths.

The script never enables Hyper-V, reboots, changes BIOS/UEFI, downloads media, installs the guest OS, creates a baseline checkpoint, or touches a host physical volume. The manifest records that guest installation and baseline setup remain user actions. Failure is fail-closed and written under `artifacts/acceptance/testlab/<run>`.
