# Cleanup-RealMachineInstallerAcceptance.ps1

Explicitly confirmed cleanup for the physical installer bundle. It can uninstall the product without removing `%ProgramData%\Storage Chronicle\history`, and can dismount/remove only VHDX files under a supplied test-data root whose sibling `.storage-chronicle-testlab-marker.json` is present. Elevation, `-ConfirmCleanup`, exact-root checks, and `ShouldProcess` are required; no broad recursive user-data deletion is permitted.

Tests: parser validation; destructive cleanup requires a human-approved disposable target.
