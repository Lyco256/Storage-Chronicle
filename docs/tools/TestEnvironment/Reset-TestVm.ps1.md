# Reset-TestVm.ps1

Restores the explicitly named `SC-CLEAN-BASELINE` checkpoint for one approved TestLab VM. It is preview-only until `-Apply` is supplied and refuses missing checkpoints, non-approved VM names, or paths outside the approved TestLab root.

Restores only the named approved TestLab VM (`SC-Test-W11` or `SC-Test-W10`) to the user-created `StorageChronicle-Baseline` checkpoint. It is read-only with respect to VM state unless explicit `-Apply` is supplied, and it fails when the checkpoint is absent rather than inventing a clean state.

It does not delete VHDX files, reset host disks, or alter Hyper-V configuration. The result is recorded under `artifacts/acceptance/testlab/<run>`.
