# Copy-TestArtifactsToVm.ps1

Copies one existing repository artifact into a guest through VirtualBox Guest Additions `VBoxManage guestcontrol copyto`. The source must be a real file; the VM must be `SC-Test-W11-VBox` or `SC-Test-W10-VBox` and the config/root/safety checks must pass. It does not use SMB, shared folders, clipboard, drag-and-drop, or a host physical volume and does not claim that the guest consumed the artifact.
