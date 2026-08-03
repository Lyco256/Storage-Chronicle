# Test-All.ps1

## Role

Runs build, fast tests, quality checks, and headless UI tests. Windows hardware acceptance is opt-in so an ordinary developer checkout cannot report physical VHDX/media/SMB/service tests as green.

## Inputs and outputs

`-RunPrivileged` invokes `Test-WindowsPrivileged.ps1`; otherwise the script records that privileged acceptance is isolated and returns success for the normal repository gate. The Unix wrapper follows the same policy with `RUN_PRIVILEGED=1` or `--run-privileged`.

## Failure behavior

Every invoked stage propagates its exit code. Privileged acceptance returns nonzero when it is not executed or when a configured capability fails.

## Tests

The script itself is exercised by the release checklist and is the normal entry point for `build/Test-All.ps1`.
