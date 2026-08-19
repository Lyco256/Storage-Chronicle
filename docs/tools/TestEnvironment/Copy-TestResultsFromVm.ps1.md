# Copy-TestResultsFromVm.ps1

Copies one real guest result into the repository's `artifacts/acceptance/testlab` tree through PowerShell Direct. It validates the destination under that artifact root and the source through the guest session. Missing VM/session/result fails; no empty or synthetic result is created.
