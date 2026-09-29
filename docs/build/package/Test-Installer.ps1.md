# Test-Installer.ps1

## Role and cases

Runs the eleven R-20 installer acceptance cases through a real Windows driver: clean install, repair, update, rollback, uninstall, failed-install rollback, history retention/reuse, service recovery, Session Agent startup, non-admin UI, and storage permission. The harness validates driver evidence; it does not synthesize behavior.

## Public inputs and owned outputs

The script has no shared product types. Parameters select MSI payloads, Windows target, physical target kind/local execution mode, driver, paths, GUID RunId, expected computer name, externally reviewed SHA-256 fingerprint of the bundle hash manifest, credential references, timeout, and explicit execution/confirmation switches. Legacy VM-related parameters/environment names are compatibility surface only; VM/non-physical execution is rejected.

Physical execution requires an elevated session on the exact declared computer, expected supported Windows target, a non-virtualized model heuristic, matching TestLab Workload/NTFS markers and fixed-volume identity, a fixture root outside system/application/repository/bundle/synchronized folders, canonical Program Files and ProgramData product paths, a clean product/service/install/ProgramData boundary, matching trusted bundle fingerprint and all payload hashes, and a new explicit evidence path outside protected/synchronized/test-data roots. Immediately before mutation it asks the user to type `I CONFIRM DEDICATED PC <computer> RUN <guid>`. This confirms the requested scope; it is not proof that the PC has no unregistered/user-profile product data or that hardware is truly physical.

On an authorized run, the harness creates a GUID-owned evidence directory and create-new receipt, logs, result JSON, manifest, and report. It does not overwrite existing evidence. Product mutations remain limited to the designated installer paths; the driver requires the run receipt, one-case nonce, host/run identity, and matching fixture volume. App-owned history is intentionally preserved at uninstall.

## Invariants and failure behavior

- The eleven case IDs are always represented; absent inputs or failed preconditions do not pass.
- Only `PhysicalMachine` + `Local` + explicit `Execute` is accepted; the virtualized installer path is disabled.
- Manifest entries must be safe, unique relative paths beneath the bundle and use 64-digit SHA-256 values. The selected base/update/rollback MSI files and driver must be the exact corresponding files in the independently fingerprinted manifest, even when this script is called directly instead of through the parent runner.
- Existing product, service, install path, or ProgramData root blocks execution. Cases after clean install require run-owned prior state; uninstall is not followed by an implicit history-root recreation.
- All evidence files use create-new semantics. Exit 0 requires all eleven real cases to pass; 1 indicates failure and 2 indicates not-executed/blocked.
- No product/user file contents or content hashes are read. Installer effects are intentionally mutating and are not allowed on an everyday machine.

## Dependencies, tests, limitations

Depends on Windows MSI/SCM/session/ACL APIs, the signed-off bundle and real driver. Static tests are in `tests/StorageChronicle.Installer.Tests/InstallerManifestTests.cs`; PowerShell parsing is a separate validation. Neither proves runtime effects, complete user-profile discovery, physical-hardware identity, TOCTOU resistance, or installer safety. Do not run until the full source audit and independent process-attributed runtime write monitor pass and the user explicitly prepares/approves the dedicated PC.
