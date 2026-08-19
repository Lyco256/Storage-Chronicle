# Test-FinalAcceptance.ps1

This is the final fail-closed evidence aggregator for the nine groups in `Requirements/21_FINAL_ACCEPTANCE_ORCHESTRATION.md`: TestLab/real I/O, confirmed reconciliation, Windows privileged capabilities, Windows 10 22H2, idle resource acceptance, MFT performance, physical installer, live Agent/Explorer correlation, and branch integration.

Each group must be supplied as a real JSON artifact with `AcceptanceEligible=true` and a non-diagnostic, non-partial, non-failed status. Missing, malformed, `NOT_EXECUTED`, `AcceptanceEligible=false`, or diagnostic artifacts produce `AcceptanceEligible=false` and exit code 2. The script performs no VM, disk, installer, branch, or network operation and cannot turn a fixture or diagnostic result into acceptance.
