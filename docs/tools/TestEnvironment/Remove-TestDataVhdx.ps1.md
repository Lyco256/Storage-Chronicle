# Remove-TestDataVhdx.ps1

Detaches and removes exactly one previously created disposable VirtualBox VDI. It requires the path to remain at the exact `data/<VM>/<TestId>-<Role>.vdi` location under the approved TestLab root, the file to be attached exactly once to the selected approved VM, and explicit `-Apply`. Missing or ambiguous attachment fails closed; no recursive or host-volume deletion is performed. The `-VhdxPath` parameter name remains for compatibility with existing callers; it accepts only the exact `.vdi` contract in this provider.
