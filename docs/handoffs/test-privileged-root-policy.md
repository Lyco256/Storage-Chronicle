# Privileged acceptance protected-root policy handoff

## Scope and changes

- Added one shared lexical path-boundary policy to `build/quality/AcceptanceContracts.ps1` for Windows/application directories, the current user profile/Documents, and OneDrive environment roots.
- `build/Test-Privileged.ps1` now applies the policy to both EvidenceRoot and disposable acceptance roots before marker-based workload preparation. Repository containment remains an explicit separate check.
- Added a no-I/O PowerShell contract test covering Windows special folders, profile-prefix boundaries, all three OneDrive variables, and parser-only validation of the privileged entry point. The test is part of `Test-Quality.ps1`.
- Updated static Installer acceptance assertions and documentation mirrors for the shared policy and test.

## Validation

- `./build/quality/Test-PrivilegedRootSafetyContracts.ps1` — passed, 20/20, including exact-root, descendant, and prefix-sibling boundaries.
- `./build/Test-All.ps1` — passed with exit code 0: full solution build, all configured Fast projects, Quality/coverage, offline safety contracts, UI; build had 0 warnings/errors.
- `git diff --check` and DocMirror — passed through the integrated validation.

## Safety boundary and limitations

- No privileged runner, file-mutation workload, installer/MSI, UAC, service, VHDX, physical media, or host preflight was run. The contract uses synthetic path strings and restores process environment values in `finally`.
- This is lexical root rejection only. It does not authenticate marker contents, make path checks atomic, prove resolved volume identity under hostile races, replace reparse/volume/role checks, or satisfy independent runtime monitoring and Requirement 37 physical acceptance. The global physical decision remains **FAIL / DO NOT RUN**.
