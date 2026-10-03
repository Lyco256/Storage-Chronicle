# Reset-TestVm.ps1

This legacy VirtualBox snapshot-reset entry point is retired by Requirement 37. It performs no VM, snapshot, disk, filesystem, or evidence I/O, including with `-Apply`; it exits with an error. Do not construct or restore whole-OS test VMs.

Restores the explicitly named `SC-CLEAN-BASELINE` VirtualBox snapshot for one approved TestLab VM. It is preview-only until `-Apply` is supplied and refuses missing snapshots, non-approved VM names, or paths outside the approved TestLab root.

Restores only the named approved TestLab VM (`SC-Test-W11-VBox` or `SC-Test-W10-VBox`) after validating its exact 4 GiB/2-vCPU/EFI/TPM/VDI profile and approved disk root, to the user-created `SC-CLEAN-BASELINE` snapshot. It is read-only with respect to VM state unless explicit `-Apply` is supplied, and it fails when the snapshot is absent rather than inventing a clean state.

It does not delete VDI/VHDX files, reset host disks, or alter host virtualization configuration. The result is recorded under `artifacts/acceptance/testlab/<run>`.
