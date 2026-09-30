# Verify-Windows10PhysicalAcceptance.ps1

Fail-closed preflight for the physical Windows 10 22H2 requirement. It records the computer name, ProductName, DisplayVersion/build, x64, administrator state, free space, a safe marked test-data root containing matching markers, and the VHDX/data-volume destination in JSON. Its computer identity must match the privileged and installer evidence used by the Windows 10 finalizer. It exits 2 on Windows 11, a non-22H2 target, x86, missing elevation, insufficient free space, or an unsafe/missing/mismatched root and always writes `AcceptanceEligible=false`; it never claims compatibility or starts product tests.

Tests: parser validation and current-host wrong-environment execution.
