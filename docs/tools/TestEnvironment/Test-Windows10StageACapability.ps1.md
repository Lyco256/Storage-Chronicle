# Test-Windows10StageACapability.ps1

## Role

Runs the Windows 10 22H2 Stage A capability probes inside the approved `SC-Test-W10` guest. It produces independent real-evidence artifacts for Cloud Files API capability detection and the no-driver invariant.

## Public inputs and outputs

The script requires an output directory, a run identifier, and the guest VM name. It writes one evidence and one check JSON file for each probe. The Cloud Files probe uses `LoadLibrary`/`GetProcAddress` against `cldapi.dll` without calling a cloud service or reading cloud history. The no-driver probe inspects Windows driver metadata, the system drivers directory filenames, and `pnputil /enum-drivers`; it never reads file contents or hashes.

## Invariants and failure behavior

The result is `PASSED` and `AcceptanceEligible=true` only for Windows 10 22H2 x64 and complete real probes. Wrong OS, missing APIs, inaccessible driver metadata, a matching product driver, or any probe exception yields `FAILED`/ineligible. Results are always `Diagnostic=false`, `EvidenceOrigin=real`, and identify `SC-Test-W10`/VM execution; an exit code alone is not acceptance evidence.

## Dependencies and tests

The script runs in a Windows guest with PowerShell, WMI, `cldapi.dll`, and `pnputil.exe`. The host orchestrator copies the resulting files back to the repository acceptance artifact directory. Tests validate the static schema, real-probe APIs, no-driver fail-closed markers, and PowerShell parser/doc-mirror rules.
