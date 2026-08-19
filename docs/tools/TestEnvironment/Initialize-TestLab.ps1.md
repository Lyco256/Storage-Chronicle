# Initialize-TestLab.ps1

Validates the user-approved TestLab config in read-only mode by default. With explicit `-Apply`, it creates or validates only `SC-Test-W11` and `SC-Test-W10` as Generation 2 VMs under the approved root, with two vCPUs, dynamic 2–6 GiB memory, 80/64 GiB OS VHDX, Windows 11 Secure Boot/vTPM, and no network adapter. It attaches only the supplied local official ISO paths.

The script never enables Hyper-V, reboots, changes BIOS/UEFI, downloads media, installs the guest OS, creates a baseline checkpoint, or touches a host physical volume. The manifest records that guest installation and baseline setup remain user actions. Failure is fail-closed and written under `artifacts/acceptance/testlab/<run>`.
