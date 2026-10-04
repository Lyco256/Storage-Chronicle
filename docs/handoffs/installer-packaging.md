# Installer and packaging handoff

## Scope

Requirement: `Requirements/20_AGENT_INSTALLER_PACKAGING.md`. The WiX project packages one x64 product containing the desktop UI, Service Agent, and Session Agent.

## Requirement 20 installer safety follow-up (branch `feat/installer-hash-hardening`)

### Source-to-sink findings and changes

- `build/package/Test-Installer.ps1` previously hashed the manifest and payloads by path, then `Get-DriverInvocation` passed the same path to elevated PowerShell and the driver passed MSI paths to `msiexec`. A writable/replacable path existed between hash and use. The parent now calls `VerifiedPayloadLock.OpenAndVerify` for the externally fingerprinted manifest and every manifest payload. Each object hashes a single open read handle configured with `FileShare.Read` and stays alive through child termination, denying new file write/delete/rename opens while the elevated child consumes the path. The owner receipt and disclosure use these recorded hashes rather than reopening MSI paths.
- `installer/StorageChronicle.wxs` authors HKLM Run value `StorageChronicleSessionAgent`. The physical harness now enumerates both 32-bit and 64-bit HKLM Run views read-only, compares the exact target value name with registry case-insensitive semantics, and refuses execution on collision or inspection error. It checks during preflight and again immediately before the first `runas` request; after a cancelled UAC launch it checks again. It performs no registry mutation.
- The parent harness now records a create-only `StorageChronicle.InstallerCaseTimeout.v1` artifact and case status `INDETERMINATE` when a bounded wait expires. It closes the broker pipe as needed, never kills the elevated child, retains all payload handles, and waits without a second timeout on that exact process handle. The case remains indeterminate, dependent cases remain `NOT_EXECUTED` even after terminal recovery, and the final harness status/exit code cannot pass.
- Top-agent continuation (2026-10-04): corrected the out-of-scope physical driver timeout. `Invoke-Captured` now retains its exact `Process` handle, records timeout status, waits for that child to exit without killing it, and returns the real exit code and configured deadline. `Invoke-Msi` then fails the current case and forbids dependent operations. Updated the static contract test, parser-checked the script, and passed the targeted installer test suite; no privileged operation was run. This only addresses the client-process termination defect; it does not prove service-side Installer completion, rollback, or complete MSI effect containment. The driver gate remains enforced before UAC.

### Handoff blocker / required owner action

- `Blocked`: physical installer acceptance cannot be enabled while the out-of-scope driver kills an MSI child on timeout.
- `Reason`: the driver owns the `msiexec` Process instance and currently terminates it at timeout, so the Requirement 20 parent cannot preserve the actual MSI process indeterminate without that termination. Waiting for the wrapper process is not sufficient while the driver has destructive timeout behavior.
- `WhyUserActionIsRequired`: no user action is required; this is a Requirement 19 ownership handoff to the top agent / owner of `tools/PhysicalAcceptance/Invoke-RealInstallerCase.ps1`.
- `DoThis`: in the Requirement 19-owned driver, replace timeout `Kill()` with a durable indeterminate record and wait on the exact MSI process handle for terminal state; keep dependent actions gated until validated terminal result evidence exists. Preserve create-only evidence and avoid inspecting it before process termination. Then update its mirrored documentation and tests and provide its reviewed source/contract test result.
- `ExpectedResult`: the driver AST contract accepts exactly one timeout boundary with bounded and terminal waits and no timeout kill; Requirement 20's parent can then remain fail-closed but proceed to the separately approved physical gate.
- `DoNotDo`: do not run MSI, UAC, service, registry-mutation, physical acceptance, or VHDX operations as part of this code handoff; do not edit Requirement 20 ownership paths from the Requirement 19 worktree.
- `ResumeCommand`: resume `feat/installer-hash-hardening` and rerun the Requirement 20 installer source-contract tests after the audited driver fix is available in its bundle.
- `SendBack`: driver diff/commit, updated docs/tests, and exact non-mutating commands/results; do not send credentials or secrets.

### Validation for this follow-up

- `dotnet test tests/StorageChronicle.Installer.Tests/StorageChronicle.Installer.Tests.csproj --no-restore --verbosity minimal`: not usable with this repository's `global.json` MTP runner configuration; it rejects the project as a VSTest runner. No installer behavior ran.
- `dotnet restore tests/StorageChronicle.Installer.Tests/StorageChronicle.Installer.Tests.csproj --verbosity minimal`: passed.
- `dotnet build tests/StorageChronicle.Installer.Tests/StorageChronicle.Installer.Tests.csproj --no-restore --verbosity minimal`: passed, zero warnings/errors.
- `tests/StorageChronicle.Installer.Tests/bin/Debug/net10.0/StorageChronicle.Installer.Tests.exe --progress off --minimum-expected-tests 1`: passed, 28/28.
- Windows PowerShell 5.1.26100.9444 parser checks for `build/package/Test-Installer.ps1` and `tools/PhysicalAcceptance/Invoke-RealInstallerCase.ps1`: passed.
- Windows PowerShell 5.1 `Add-Type` plus read-only `VerifiedPayloadLock.OpenAndVerify` of its own source file and parent directory chain: passed.
- Windows PowerShell 5.1 execution of only the extracted `Assert-BundledDriverTimeoutContract` function: refused the current driver on its timeout `Kill()` call before UAC, as intended. The invoked function only parsed/read source; it did not start the driver.
- `git diff --check`: passed. Changed paths are limited to Requirement 20 owned `build/package/**`, `tests/StorageChronicle.Installer.Tests/**`, their mirrored `docs/build/package/**`, and this handoff.

No MSI, installer, service, UAC, registry mutation, physical test, or VHDX operation was run. A same-user forced termination of the non-elevated parent releases its held handles; held-handle protection assumes the harness remains alive through the exact child-process wait and is not equivalent to an ACL-protected staging directory surviving parent termination.

## Implemented contract

- The installer does not install a filesystem minifilter or kernel driver.
- Agent service recovery actions are distinct and retain the history root through repair, update, uninstall, and rollback.
- Installation is per-machine and the Session Agent remains scoped to the interactive user session.
- Manifest tests enforce product identity, service recovery, no-driver behavior, and history retention declarations.
- `build/package/Test-Installer.ps1` now defines the eleven R-20 acceptance cases, requires an explicit `TargetKind`, and writes JSON/Markdown/per-case evidence. It is fail-closed: a build artifact or driver exit code alone cannot produce a pass, and a VM result cannot satisfy the physical-machine final gate.
- `tools/PhysicalAcceptance/Invoke-VirtualBoxInstallerCase.ps1` is now a host-side Guest Additions guest driver, and `tools/TestEnvironment/Run-VirtualBoxInstallerAcceptance.ps1` orchestrates one approved Windows 11/Windows 10 VM matrix from the clean baseline with explicit guest credentials, marked guest test-root, result transfer, per-case baseline restore, and cleanup. Both remain fail-closed and do not claim a matrix pass without guest evidence.

## Verification

## Physical UAC broker integration (implemented; privileged acceptance still gated)

### Follow-up: pin the authorization helper before compilation (2026-10-04)

The manual bundle now includes `InstallerAuthorizationProtocol.cs`, and the real-machine bundle preflight requires it in the payload hash manifest. Before any physical-case UAC request, `Test-Installer.ps1` reads the externally fingerprinted manifest through a read-only handle, checks its schema, hashes the helper bytes read through a read-only handle against the manifest entry, and compiles that exact in-memory source. It then opens and verifies the manifest and all payloads with `VerifiedPayloadLock` before proceeding. Source-contract tests guard the bundle membership and verification/compile/lock ordering. This closes the harness-helper source-loading gap; it does not establish that the MSI's complete side effects are safe. No MSI, UAC, service, registry-mutation, or physical acceptance operation was run for this follow-up.

Validation: Windows PowerShell 5.1 parser checks passed for the changed harness, bundle generator, and real-machine entry point; `dotnet build tests/StorageChronicle.Installer.Tests/StorageChronicle.Installer.Tests.csproj --no-restore --verbosity minimal` passed with 0 warnings/errors; the MTP test executable passed 28/28; `build/Test-All.ps1` passed (Build, Fast, Quality/coverage, and UI, exit 0); `build/quality/Test-DocMirror.ps1` passed; and `git diff --check` passed. The first focused test run caught a residual `Add-Type -Path` in a dormant fallback loader (27/28); the fallback now refuses unverified loading and the full rerun passed. No MSI, UAC, service, registry-mutation, privileged, or physical acceptance operation was run. `build/Test-All.ps1` intentionally skipped the isolated privileged Windows acceptance gate.

The `Test-Installer.ps1` parent-side contract is implemented on `feat/installer-packaging`. It remains ordinary-integrity and requires a filtered token for a local Administrators member, `EnableLUA=1`, and `ConsentPromptBehaviorAdmin` in `{1,2,3,4}`; values 0, 5, absent, or unknown fail closed. Both the launcher and parent enforce this policy gate without tests reading current host policy. The parent discloses each action and exact MSI hashes and accepts one exact phrase before UAC. It creates a per-case random pipe with an explicit current-user-SID-only DACL through Windows PowerShell 5.1-compatible `NamedPipeServerStreamAcl.Create` (arg 10 is numeric zero), verifies the kernel client PID and Hello, then sends one newline-delimited Grant. The phrase is carried only in that authenticated Grant; it is not passed through argv/stdin, and the nonce is not inherited through child environment. UAC cancellation is `NOT_EXECUTED`; a live timeout child is not killed and blocks later cases.

Wire names are `StorageChronicle.InstallerCaseAuthorizationHello.v1` and `StorageChronicle.InstallerCaseAuthorizationGrant.v1`, matching `docs/handoffs/installer-physical-guard.md`. Parent result consumption expects create-only JSON schemas `StorageChronicle.InstallerCaseResult.v1` and `StorageChronicle.InstallerCaseLog.v1`, each carrying `ComputerName`, `RunId`, and `CaseId`; the top-agent-owned driver must emit these fields before integration. The result is read only after the exact elevated child exits. UAC UI behavior remains unverified and no physical acceptance operation is authorized by these changes.

- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/package/Build-Installer.ps1`: passed on the current 2026-08-03 checkout with self-contained Agent/Session Agent/UI publish and WiX Toolset SDK 6.0.2; MSI build completed with 0 warnings/errors at `installer/bin/x64/Release/StorageChronicle.msi`.
- `tests/StorageChronicle.Installer.Tests/`: manifest tests pass in the non-privileged test gate.
- `build/package/Test-Installer.ps1` without a disposable target: all eleven cases recorded `NOT_EXECUTED`, exit code 2; no physical acceptance was misreported.
- 2026-10-03 verification of the UAC broker implementation: installer tests 23/23, DocMirror passed, parent/driver PowerShell AST parsing passed, full Debug solution build passed with 0 warnings/errors, Fast passed all 24 projects after solution restore/build, and `build/Test-All.ps1` passed (Build, Fast, Quality/coverage, and UI). Initial Fast attempt before restoring/building the architecture dependencies failed; reruns passed.
- No UAC prompt or privileged/physical test was run. The loaded PowerShell driver hash-to-load race, MSI path hash-to-use race, MSI effect containment, end-to-end installer acceptance, and dedicated-PC safety gates remain unresolved; this is not release approval.

## Release-environment work still required

The MSI must first be exercised through complete Windows 11 and Windows 10 VirtualBox matrices, then on a clean Windows 10/11 x64 physical machine through install, repair, update, rollback, uninstall, service start/recovery, interactive Session Agent startup, non-admin UI launch, and history retention checks. The repository now has the fail-closed generic matrix contract, physical-machine driver, VirtualBox guest driver, and host orchestrator; the VM driver still requires an approved running TestLab VM, Guest Additions, guest credential, guest-local non-admin credential reference, marked guest test root, MSI/update/rollback payloads, and a true interactive Session Agent target for that case. Until both VM matrices and the physical runs produce acceptance manifests, packaging is build-verified but not release-verified.
