# Invoke-WindowsTestLab.ps1

Retired VM/guest acceptance launcher. It exits with status 2 before loading helper scripts, creating evidence directories, invoking VirtualBox, touching guest settings, creating or formatting disks, or starting a workload. The legacy implementation remains below the unconditional exit for historical reference only; it is unreachable during normal PowerShell execution.

This file is retained so old callers fail explicitly instead of silently using an obsolete acceptance path. Do not invoke it for testing. Requirement 37 supersedes VM evidence with physical-machine acceptance and requires current-commit static review, independent process-attributed write monitoring, and read-only host preflight before any product or privileged run. The current physical workflow and final evidence contract remain incomplete, so physical acceptance is still `NOT_EXECUTED`.

## Contract

- Inputs: legacy parameters are accepted by the parser for compatibility but ignored.
- Outputs: an error explaining retirement and exit code 2; no files or host/guest state are changed.
- Dependencies: PowerShell only before the fail-closed exit; no helper or external command is loaded or called.
- Invariant: the exit occurs before `TestLab.Common.ps1` is imported.
- Failure behavior: all invocations fail closed. This is not a replacement physical runner.
- Tests: `InstallerManifestTests.RetiredTestLabAndAgentModeFailClosedBeforeSideEffects` checks the early exit and the Agent argument guard.
- Settings: test configuration must use the supported product settings UI/API. Direct machine-settings file writes are not an approved setup path.
