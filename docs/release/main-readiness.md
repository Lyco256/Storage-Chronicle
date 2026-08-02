# Main readiness

Before promoting `devenv` to `main`, run:

1. `dotnet restore StorageChronicle.slnx --ignore-failed-sources`
2. `dotnet build StorageChronicle.slnx --no-restore`
3. `build/Test-All.ps1`
4. `build/package/Build-Installer.ps1` on a machine with WiX 6.0.2
5. Windows 10/11 privileged and installer acceptance matrices

The integration branch must be clean, have a handoff, contain no generated artifacts, and be merged with `--no-ff`. History is never removed during installation or uninstall.
