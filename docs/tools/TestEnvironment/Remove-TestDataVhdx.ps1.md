# Remove-TestDataVhdx.ps1

Detaches and removes exactly one previously created disposable data VHDX. It requires the path to remain at the exact `data/<VM>/<TestId>-<Role>.vhdx` location under the approved TestLab root, the file to be attached exactly once to the selected approved VM, and explicit `-Apply`. Missing or ambiguous attachment fails closed; no recursive or host-volume deletion is performed.
