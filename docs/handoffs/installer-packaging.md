# Installer and packaging handoff

## Scope

Requirement: `Requirements/20_AGENT_INSTALLER_PACKAGING.md`. The WiX project packages one x64 product containing the desktop UI, Service Agent, and Session Agent.

## Implemented contract

- The installer does not install a filesystem minifilter or kernel driver.
- Agent service recovery actions are distinct and retain the history root through repair, update, uninstall, and rollback.
- Installation is per-machine and the Session Agent remains scoped to the interactive user session.
- Manifest tests enforce product identity, service recovery, no-driver behavior, and history retention declarations.
- `build/package/Test-Installer.ps1` now defines the eleven R-20 acceptance cases, requires an explicit `TargetKind`, and writes JSON/Markdown/per-case evidence. It is fail-closed: a build artifact or driver exit code alone cannot produce a pass, and a VM result cannot satisfy the physical-machine final gate.
- `tools/PhysicalAcceptance/Invoke-VirtualBoxInstallerCase.ps1` is now a host-side Guest Additions guest driver, and `tools/TestEnvironment/Run-VirtualBoxInstallerAcceptance.ps1` orchestrates one approved Windows 11/Windows 10 VM matrix from the clean baseline with explicit guest credentials, marked guest test-root, result transfer, per-case baseline restore, and cleanup. Both remain fail-closed and do not claim a matrix pass without guest evidence.

## Verification

## Physical UAC broker integration (implemented; privileged acceptance still gated)

The `Test-Installer.ps1` parent-side contract is implemented on `feat/installer-packaging`. It remains ordinary-integrity and requires a filtered token for a local Administrators member, `EnableLUA=1`, and `ConsentPromptBehaviorAdmin` in `{1,2,3,4}`; values 0, 5, absent, or unknown fail closed. Both the launcher and parent enforce this policy gate without tests reading current host policy. The parent discloses each action and exact MSI hashes and accepts one exact phrase before UAC. It creates a per-case random pipe with an explicit current-user-SID-only DACL through Windows PowerShell 5.1-compatible `NamedPipeServerStreamAcl.Create` (arg 10 is numeric zero), verifies the kernel client PID and Hello, then sends one newline-delimited Grant. The phrase is carried only in that authenticated Grant; it is not passed through argv/stdin, and the nonce is not inherited through child environment. UAC cancellation is `NOT_EXECUTED`; a live timeout child is not killed and blocks later cases.

Wire names are `StorageChronicle.InstallerCaseAuthorizationHello.v1` and `StorageChronicle.InstallerCaseAuthorizationGrant.v1`, matching `docs/handoffs/installer-physical-guard.md`. Parent result consumption expects create-only JSON schemas `StorageChronicle.InstallerCaseResult.v1` and `StorageChronicle.InstallerCaseLog.v1`, each carrying `ComputerName`, `RunId`, and `CaseId`; the top-agent-owned driver must emit these fields before integration. The result is read only after the exact elevated child exits. UAC UI behavior remains unverified and no physical acceptance operation is authorized by these changes.

- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/package/Build-Installer.ps1`: passed on the current 2026-08-03 checkout with self-contained Agent/Session Agent/UI publish and WiX Toolset SDK 6.0.2; MSI build completed with 0 warnings/errors at `installer/bin/x64/Release/StorageChronicle.msi`.
- `tests/StorageChronicle.Installer.Tests/`: manifest tests pass in the non-privileged test gate.
- `build/package/Test-Installer.ps1` without a disposable target: all eleven cases recorded `NOT_EXECUTED`, exit code 2; no physical acceptance was misreported.
- 2026-10-03 verification of the UAC broker implementation: installer tests 23/23, DocMirror passed, parent/driver PowerShell AST parsing passed, full Debug solution build passed with 0 warnings/errors, Fast passed all 24 projects after solution restore/build, and `build/Test-All.ps1` passed (Build, Fast, Quality/coverage, and UI). Initial Fast attempt before restoring/building the architecture dependencies failed; reruns passed.
- No UAC prompt or privileged/physical test was run. The loaded PowerShell driver hash-to-load race, MSI path hash-to-use race, MSI effect containment, end-to-end installer acceptance, and dedicated-PC safety gates remain unresolved; this is not release approval.

## Release-environment work still required

The MSI must first be exercised through complete Windows 11 and Windows 10 VirtualBox matrices, then on a clean Windows 10/11 x64 physical machine through install, repair, update, rollback, uninstall, service start/recovery, interactive Session Agent startup, non-admin UI launch, and history retention checks. The repository now has the fail-closed generic matrix contract, physical-machine driver, VirtualBox guest driver, and host orchestrator; the VM driver still requires an approved running TestLab VM, Guest Additions, guest credential, guest-local non-admin credential reference, marked guest test root, MSI/update/rollback payloads, and a true interactive Session Agent target for that case. Until both VM matrices and the physical runs produce acceptance manifests, packaging is build-verified but not release-verified.
