# Installer and packaging handoff

## Scope

Requirement: `Requirements/20_AGENT_INSTALLER_PACKAGING.md`. The WiX project packages one x64 product containing the desktop UI, Service Agent, and Session Agent.

## Implemented contract

- The installer does not install a filesystem minifilter or kernel driver.
- Agent service recovery actions are distinct and retain the history root through repair, update, uninstall, and rollback.
- Installation is per-machine and the Session Agent remains scoped to the interactive user session.
- Manifest tests enforce product identity, service recovery, no-driver behavior, and history retention declarations.
- `build/package/Test-Installer.ps1` now defines the eleven R-20 acceptance cases, requires an explicit `TargetKind`, and writes JSON/Markdown/per-case evidence. It is fail-closed: a build artifact or driver exit code alone cannot produce a pass, and a VM result cannot satisfy the physical-machine final gate.
- `tools/PhysicalAcceptance/Invoke-HyperVInstallerCase.ps1` is now a host-side PowerShell Direct guest driver, and `tools/TestEnvironment/Run-HyperVInstallerAcceptance.ps1` orchestrates one approved Windows 11/Windows 10 VM matrix from the clean baseline with explicit guest credentials, marked guest test-root, result transfer, and cleanup. Both remain fail-closed and do not claim a matrix pass without guest evidence.

## Verification

- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/package/Build-Installer.ps1`: passed on the current 2026-08-03 checkout with self-contained Agent/Session Agent/UI publish and WiX Toolset SDK 6.0.2; MSI build completed with 0 warnings/errors at `installer/bin/x64/Release/StorageChronicle.msi`.
- `tests/StorageChronicle.Installer.Tests/`: manifest tests pass in the non-privileged test gate.
- `build/package/Test-Installer.ps1` without a disposable target: all eleven cases recorded `NOT_EXECUTED`, exit code 2; no physical acceptance was misreported.

## Release-environment work still required

The MSI must first be exercised through complete Windows 11 and Windows 10 Hyper-V matrices, then on a clean Windows 10/11 x64 physical machine through install, repair, update, rollback, uninstall, service start/recovery, interactive Session Agent startup, non-admin UI launch, and history retention checks. The repository now has the fail-closed generic matrix contract, physical-machine driver, Hyper-V guest driver, and host orchestrator; the VM driver still requires an approved running TestLab VM, guest credential, marked guest test root, MSI/update/rollback payloads, and a true interactive Session Agent target for that case. Until both VM matrices and the physical runs produce acceptance manifests, packaging is build-verified but not release-verified.
