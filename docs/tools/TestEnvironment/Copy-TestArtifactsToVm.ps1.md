# Copy-TestArtifactsToVm.ps1

Copies one existing repository artifact into a guest through a PowerShell Direct session. The source must be a real file; the VM must be one of the approved TestLab names and the config/root checks must pass. It does not use SMB or a host physical volume and does not claim that the guest consumed the artifact.
