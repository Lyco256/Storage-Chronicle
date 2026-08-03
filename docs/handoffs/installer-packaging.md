# Installer and packaging handoff

## Scope

Requirement: `Requirements/20_AGENT_INSTALLER_PACKAGING.md`. The WiX project packages one x64 product containing the desktop UI, Service Agent, and Session Agent.

## Implemented contract

- The installer does not install a filesystem minifilter or kernel driver.
- Agent service recovery actions are distinct and retain the history root through repair, update, uninstall, and rollback.
- Installation is per-machine and the Session Agent remains scoped to the interactive user session.
- Manifest tests enforce product identity, service recovery, no-driver behavior, and history retention declarations.
- `build/package/Test-Installer.ps1` now defines the ten R-20 acceptance cases and writes JSON/Markdown/per-case evidence. It is fail-closed: a build artifact or driver exit code alone cannot produce a pass.

## Verification

- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/package/Build-Installer.ps1`: MSI build previously passed with WiX Toolset SDK 6.0.2, 0 warnings/errors.
- `tests/StorageChronicle.Installer.Tests/`: manifest tests pass in the non-privileged test gate.
- `build/package/Test-Installer.ps1` without a disposable target: all ten cases recorded `NOT_EXECUTED`, exit code 2; no physical acceptance was misreported.

## Release-environment work still required

The MSI must be exercised on a clean Windows 10/11 x64 machine through install, repair, update, rollback, uninstall, service start/recovery, interactive Session Agent startup, non-admin UI launch, and history retention checks. Until those runs produce an acceptance manifest, packaging is build-verified but not release-verified.
