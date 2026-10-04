# Run-VirtualBoxInstallerAcceptance.ps1

This legacy VirtualBox installer orchestrator is retired under Requirements 26 and 37. It fails closed before loading TestLab helpers and performs no VM, snapshot, provisioning, installer, service, file, or evidence operation, regardless of `-Apply`.

Windows 10 compatibility and installer acceptance require the physical workflows and evidence contracts; this script cannot generate eligible evidence.
