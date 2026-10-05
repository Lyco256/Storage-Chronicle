# Test-PrivilegedRootSafetyContracts.ps1

## Role

Exercises the shared acceptance protected-path predicate without dot-sourcing or invoking the privileged runner. It uses current Windows special-folder names and synthetic OneDrive roots represented only as strings.

## Public behavior

Checks that descendants of the current profile, Documents, ProgramData, Windows, Program Files, and common-program folders are rejected; verifies a profile-prefix sibling remains outside the protected boundary; and checks descendants and prefix siblings for OneDrive, OneDriveCommercial, and OneDriveConsumer. It also parses `Test-Privileged.ps1` without executing it.

## Invariants and dependencies

The test restores every process environment variable it temporarily changes in a `finally` block. It performs no directory creation, file mutation, VHDX operation, process launch, or privileged action. It depends on `AcceptanceContracts.ps1` and PowerShell's parser API.

## Failure behavior

Any predicate mismatch or parse error terminates the script with a non-zero result and a concise diagnostic.

## Tests

Run directly with `./build/quality/Test-PrivilegedRootSafetyContracts.ps1`; it is also included in `Test-Quality.ps1` and therefore in the repository's full validation lane.
