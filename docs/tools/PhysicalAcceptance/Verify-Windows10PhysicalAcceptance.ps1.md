# Verify-Windows10PhysicalAcceptance.ps1

Fail-closed preflight for the Stage B physical Windows 10 22H2 requirement. It records ProductName, DisplayVersion/build, x64, administrator state, and the approved non-system test-data root containing the TestLab `.storage-chronicle-testlab-marker.json` in JSON. It exits 2 on Windows 11, a non-22H2 target, x86, missing elevation, or an unsafe/missing/unmarked root and always writes `AcceptanceEligible=false`; it never claims compatibility or starts product tests.

Tests: parser validation and current-host wrong-environment execution.
