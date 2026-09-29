# Main readiness

The top Codex promotes only a clean integration branch with no generated artifacts. The required merge sequence is `feat/*` review → `devenv` with `--no-ff` → final gates → `main` with `--no-ff`.

## Acceptance evidence recorded on 2026-08-02

- `dotnet build StorageChronicle.slnx --no-restore -v:minimal`: passed, 0 warnings, 0 errors.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/Test-All.ps1`: passed; full solution fast run reported 174 passed, 0 failed, 0 skipped. Architecture, integration, E2E, headless, UI, documentation, and coverage gates also passed inside the script.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/quality/Test-Privileged.ps1`: passed; the available NTFS privileged test `QueriesExistingJournalWithoutCreatingOrResizing` passed. Physical VHDX, media insertion, SMB, ETW, service, and Windows 10 matrices remain environment-bound and are not claimed as run.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/quality/Test-Performance.ps1 -Filter '*EventStackPage100K*'`: passed; `EventStackPage100K` mean 4.523 ms, 99.9% CI 4.381–4.664 ms, 3.44 MiB managed allocation.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/package/Build-Installer.ps1`: passed; WiX Toolset SDK 6.0.2 produced the x64 MSI with 0 warnings and 0 errors.
- Resource acceptance run: Agent and Session Agent combined peak Private Working Set 14.53125 MiB and average CPU 0.0110659393%; both thresholds passed. Full data is in `docs/release/performance-baseline.md`.
- `build/quality/Test-DocMirror.ps1`: passed, including all source mirrors added during integration.

## Environment-bound release checks

Windows 10 22H2 x64 and a physical clean install/update/rollback/media matrix are explicitly defined but unavailable on the current host. `docs/release/windows10-compatibility.md` therefore records `boundary verified; acceptance run pending`; it must not be changed to “verified” without the corresponding run. The implementation uses the Windows 10 API boundary and capability detection, and the privileged suite is isolated so these external checks can be executed without weakening the default test gate.

History is never deleted during install, update, repair, or uninstall. No driver is installed, and no generated `artifacts/`, `bin/`, `obj/`, test result, benchmark, secret, or machine-specific file may be staged.
