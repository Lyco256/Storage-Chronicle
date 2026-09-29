# Test-WindowsPrivileged.sh

## Role

Provides the Unix-side entry point for the Windows privileged acceptance runner without silently claiming hardware coverage.

## Inputs and outputs

The script runs only when `STORAGE_CHRONICLE_RUN_PRIVILEGED_ACCEPTANCE=1` and delegates to PowerShell. Otherwise it reports that the acceptance suite was not executed.

## Failure behavior

Missing PowerShell or an invoked acceptance failure returns a nonzero exit code.

## Tests

The delegated runner records a JSON capability manifest for VHDX, USN, MFT, ETW, SMB, service, session, and removable-media checks.
