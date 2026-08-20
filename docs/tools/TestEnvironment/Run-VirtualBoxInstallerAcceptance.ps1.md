# Run-VirtualBoxInstallerAcceptance.ps1

Orchestrates the real eleven-case installer matrix for one selected Windows 11 or Windows 10 22H2 VirtualBox guest. It validates the official ISO, approved TestLab root, exact VM identity, dynamic OS disks, `SC-CLEAN-BASELINE`, resources, MSI inputs, and local-only guest credential reference. Without `-Apply` it writes `READY_FOR_USER_APPLY` and exits 2.

With explicit `-Apply`, the driver restores the clean baseline before every case, starts one VM with exactly 4096 MiB/2 vCPUs and networking disconnected, runs the existing generic `Test-Installer.ps1` harness with `TargetKind=VirtualBoxVm`/`ExecutionMode=VM`, requires exactly the defined eleven case IDs and all `PASSED`, then stops and restores the baseline in `finally`. Windows 11 and Windows 10 are selected separately; the orchestrator never starts both VMs simultaneously.

The output schema is `StorageChronicle.VirtualBoxInstallerAcceptance.v1`. Missing prerequisites, a failed case, duplicate/extra case IDs, a non-real result, or cleanup failure sets `AcceptanceEligible=false` and a nonzero exit code. Guest setup, Guest Additions, the guest local administrator, the guest-local non-admin credential reference, and creation of exactly one `SC-CLEAN-BASELINE` snapshot remain user-controlled setup steps.
