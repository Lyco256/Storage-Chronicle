# WindowsServiceRecoveryConfigurator.cs

Applies the Windows Service restart policy only through the explicit MSI-only Agent command-line mode. It refuses to modify a service unless its configured executable path matches the running executable and its service account/type match Storage Chronicle's LocalSystem own-process service.

## Role

This Windows-specific helper confines a machine configuration change to the explicit installer sequence. It is not called during ordinary service startup.

## Public types and responsibilities

The internal `WindowsServiceRecoveryConfigurator` owns one responsibility: configure three restart actions with 5, 15, and 60 second delays and a one-day reset period after validating the service image identity. Its public `TryConfigureInstalledService` method is internal to the Agent assembly and reports failure rather than claiming success.

## Inputs and outputs

It reads the current process image path and the Service Control Manager configuration for the fixed `StorageChronicleAgent` service. It changes only that service's failure actions when the registration is an own-process LocalSystem service with the same executable path. It reads no file contents and hashes no files.

## Dependencies

Uses Windows Service Control Manager P/Invoke, native service configuration structs, and standard path comparison. It is isolated in the Windows Agent project and is called by `Program.Main` only for `--configure-service-recovery`.

## Invariants

Ordinary Agent startup never invokes this helper. The helper fails closed for non-Windows execution, missing service, insufficient access, an unexpected service account/type, or a binary path mismatch. Recovery actions are exactly 5s/15s/60s.

## Threading and lifetime

The helper is synchronous and short-lived. It owns and closes its SCM/service handles and frees its unmanaged action/configuration buffers on every path.

## Failure behavior

SCM query/configuration errors return `false`; the MSI custom action uses checked-return semantics so installation cannot claim the recovery policy was applied when configuration failed.

## Tests

`tests/StorageChronicle.Installer.Tests/InstallerManifestTests.cs` statically verifies the 5/15/60-second values, executable-identity check, and MSI sequencing. Physical service behavior remains part of the explicitly gated installer acceptance matrix and is not run by fast tests.

## Change-sensitive contracts

Do not call this helper during ordinary Agent startup, weaken the registered-image/account checks, or make the MSI custom action ignore its return code.

## OS constraints

Windows only. The explicit installer command runs elevated through Windows Installer; normal service operation does not require or attempt this machine-configuration path.
