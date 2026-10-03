# Invoke-WindowsTestLab.ps1

Retired VM/guest acceptance launcher. It is now a small fail-closed stub that exits with status 2 before loading helper scripts, creating evidence directories, invoking VirtualBox, touching guest settings, creating or formatting disks, or starting a workload. No legacy VM mutation implementation remains in this file.

This file is retained so old callers fail explicitly instead of silently using an obsolete acceptance path. Do not invoke it for testing. Requirement 37 supersedes VM evidence with physical-machine acceptance and requires current-commit static review, independent process-attributed write monitoring, and read-only host preflight before any product or privileged run. The current physical workflow and final evidence contract remain incomplete, so physical acceptance is still `NOT_EXECUTED`.

## Contract

- Inputs: legacy parameters are accepted by the parser for compatibility but ignored.
- Outputs: an error explaining retirement and exit code 2; no files or host/guest state are changed.
- Dependencies: PowerShell only before the fail-closed exit; no helper or external command is loaded or called.
- Invariant: the script contains no TestLab helper import, VirtualBox invocation, disk mutation, or workload launch.
- Failure behavior: all invocations fail closed. This is not a replacement physical runner.
- Tests: `InstallerManifestTests.RetiredTestLabAndAgentModeFailClosedBeforeSideEffects` checks the early exit and the Agent argument guard.
- Settings: test configuration must use the supported product settings UI/API. Direct machine-settings file writes are not an approved setup path.
