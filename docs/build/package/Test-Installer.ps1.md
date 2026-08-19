# Test-Installer.ps1

## Role

Runs the R-20 physical installer acceptance matrix for one explicitly declared Windows target. It is intentionally separate from `build/package/Build-Installer.ps1`: a successful MSI build does not prove installation, service, session, ACL, update, rollback, or history-retention behavior.

## Public types and responsibilities

The PowerShell script has no shared product contract or substitute domain type. Its public command-line parameters select the MSI artifacts, target (`Windows10-22H2` or `Windows11`), target kind (`PhysicalMachine` or `HyperVVm`), execution mode (`Local` or `VM`), isolated-environment driver, guest paths, and non-secret credential references. The fixed manifest contains these eleven cases:

1. clean install
2. repair
3. update
4. rollback
5. uninstall
6. intentionally failed install rollback
7. history retention and reinstall reuse
8. LocalSystem service and recovery
9. Session Agent startup
10. non-admin UI
11. storage permission

The external driver performs the destructive MSI operations and state assertions in the disposable target. The harness validates the driver's result rather than replacing those operations with fake data.

## Inputs and outputs

Parameters may be supplied directly or through process environment variables. The important variables are `STORAGE_CHRONICLE_INSTALLER_MSI`, `STORAGE_CHRONICLE_INSTALLER_UPDATED_MSI`, `STORAGE_CHRONICLE_INSTALLER_ROLLBACK_MSI`, `STORAGE_CHRONICLE_INSTALLER_TARGET_OS`, `STORAGE_CHRONICLE_INSTALLER_TARGET_KIND`, `STORAGE_CHRONICLE_INSTALLER_MODE`, `STORAGE_CHRONICLE_INSTALLER_DRIVER`, `STORAGE_CHRONICLE_INSTALLER_VM_NAME`, `STORAGE_CHRONICLE_INSTALLER_WINDOWS_ISO`, `STORAGE_CHRONICLE_INSTALLER_NONADMIN_USER`, `STORAGE_CHRONICLE_INSTALLER_NONADMIN_CREDENTIAL_REF`, `STORAGE_CHRONICLE_INSTALLER_SESSION_USER`, and `STORAGE_CHRONICLE_INSTALLER_SERVICE_CREDENTIAL_REF`. No plaintext password is accepted or written to the manifest.

`-Execute` is required to arm a run. VM mode requires a running Hyper-V VM, a Windows ISO, administrator rights, and a driver; local mode additionally requires `-AllowLocalIsolatedExecution` and a disposable host whose OS matches the requested target. The default output directory is `artifacts/installer/acceptance`.

Each run writes one JSON manifest and one Markdown report named `installer-acceptance-<run-id>.*`. The JSON includes `AcceptanceEligible=true` only when all eleven cases are `PASSED`; missing, diagnostic, or partial runs remain ineligible. Driver logs and per-case result JSON files are written below the matching run directory. `-CaseTimeoutSeconds` bounds each driver invocation (1 through 7200 seconds; the default is 1800). A driver result is accepted as `PASSED` only when it names the exact case, declares the requested target and isolation, declares administrator execution, contains passing assertions, and references existing host-visible evidence files.

## Dependencies

The script depends on Windows MSI/SCM/session/ACL capabilities, PowerShell, the existing WiX output from `build/package/Build-Installer.ps1`, and an environment-specific driver that can reset a disposable VM or explicitly isolated host for each case. VM mode also depends on Hyper-V `Get-VM` and a supplied Windows ISO. The product installer remains the only product dependency; this harness does not install a driver, .NET runtime, or unrelated resident application.

## Invariants

- The eleven R-20 cases are always represented in the manifest, including when none can run.
- Missing MSI artifacts, target OS, driver, ISO, VM, isolation declaration, account, credential reference, or administrator/service capability produces `NOT_EXECUTED`, never `PASSED`.
- A nonzero exit code is returned for `FAILED` or `NOT_EXECUTED`; only eleven passed cases produce exit code `0`.
- VM results must identify the VM and the requested Windows target; physical results must declare `TargetKind=PhysicalMachine` and `ExecutionMode=Local`. A driver exit code alone is insufficient evidence.
- History is checked by the driver as retained state; the harness never adds a history-deletion operation.
- No file contents or file-content hashes are read by the harness. Evidence is checked only by path existence, and credential references are recorded without secret material.

## Threading and lifetime

Each case is executed synchronously through a bounded process timeout (`-CaseTimeoutSeconds`, default 1800 seconds). Standard output and error are drained asynchronously while the driver runs, then stored in that case's log. A timed-out driver is `FAILED`; the remaining cases are still represented and are attempted when their preconditions permit.

## Failure behavior

Exit code `0` means every case passed. Exit code `1` means the harness or a case failed. Exit code `2` means one or more cases were `NOT_EXECUTED`, including a non-Windows host, absent ISO/VM, missing service privilege, missing account/credential reference, absent MSI, absent driver, or an unarmed run. Artifact-writing failures are also nonzero. There is no dry-run success path.

## Tests

The deterministic installer manifest contract is covered by `tests/StorageChronicle.Installer.Tests/InstallerManifestTests.cs`. Physical execution is intentionally environment-bound and must be run with this script on disposable Windows 10 22H2 x64 and Windows 11 x64 targets. The generated JSON and Markdown reports are the acceptance evidence for `Requirements/20_AGENT_INSTALLER_PACKAGING.md`.

## OS constraints

MSI, LocalSystem service recovery, user-logon Session Agent startup, non-administrator UI launch, and Windows ACL behavior are Windows-only. `Windows10-22H2` and `Windows11` are separate target values; the script never labels an unexecuted Windows 10 run as compatible. VM mode requires a running Hyper-V target and a Windows ISO so the environment can be recreated or audited. Local mode is allowed only with an explicit disposable-host acknowledgement.

## Change-sensitive contracts

Keep the eleven case IDs stable because release reports and `docs/release/requirements-verification.md` refer to them. Keep `TargetKind` explicit so a VM artifact cannot satisfy the physical-installer group. Changes to the driver result schema must remain backward-incompatible only through a new `Schema` or script version. Do not weaken the isolated-target, administrator, assertion, evidence-path, or fail-closed checks to make a physical acceptance run pass. Changes to the WiX product contract belong in `installer/**` and its manifest tests, not in this harness.
