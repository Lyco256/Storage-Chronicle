# Handoff: explorer-scenario-safety

## Changes

- Hardened `tools/TestEnvironment/New-ExplorerCorrelationScenario.ps1` before any output: reject protected Windows/user/synchronized/application roots and the repository; require a fresh root containing exactly the two ordinary TestLab ownership markers; and keep marker/live-volume/TestId checks.
- Changed no-`-Apply` mode to print its `READY_FOR_USER_APPLY` JSON to stdout only. It performs no file or directory creation and rejects `OutputPath`.
- Restricted `-Apply` output to a direct child plan file in the new, run-owned scenario directory. Existing scenario directories and occupied outputs are refused; files continue to use `CreateNew`.
- Added Architecture source-contract assertions and synchronized the script explanation under `docs/tools/`.

## Validation

- PowerShell AST parse of `New-ExplorerCorrelationScenario.ps1` — passed without executing the script.
- Initial `Test-Fast.ps1 -NoRestore` — could not start because a fresh worktree had no NuGet `project.assets.json` files.
- Restored `Test-Fast.ps1` — the Architecture project exposed its existing dependency on all solution assemblies being built first; three assembly-loading tests failed while the script had only built its narrow project subset. This is not counted as a passing run.
- `./build/Test-All.ps1` — passed, exit code 0. Full build had 0 warnings and 0 errors; all configured Fast tests, Quality/coverage, offline safety contracts, retired-VM contracts, and UI passed. Architecture 18/18, Agent 72/72, Installer 28/28, Storage 22/22, MFT provenance contracts 31/31, protected-root contracts 20/20. Privileged Windows acceptance was not run.
- `./build/quality/Test-DocMirror.ps1` — passed.
- `git diff --check` — passed.

## Safety boundary and limitations

- The fixture producer was not executed. No Product process, Explorer operation, user/media data, VHDX, service, installer, UAC, or privileged runner was started.
- This change narrows lexical roots and output destinations only. It does not authenticate forgeable marker JSON or eliminate path/volume TOCTOU; Requirement 37 remains **FAIL / DO NOT RUN** pending exhaustive source-to-sink review, independent runtime write monitoring, host preflight, and all environment-bound acceptance.
- Merge into `devenv` is top-agent-only. `main` must remain untouched.
