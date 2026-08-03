# Test-AotCompatibility.ps1

## Role

Provides an explicit, opt-in Native AOT compatibility publication check for the Windows Agent and Session Agent. It uses the normal self-contained `win-x64` publication path and records per-project results below `artifacts/aot-compatibility`.

## Inputs and outputs

The `-Configuration` parameter accepts `Debug` or `Release`. `-OutputDirectory` selects an artifact-only output directory. The script does not alter the normal publication used by the installer and does not publish the UI.

## Invariants and failure behavior

The common build properties enable `PublishAot` only for the two named agent projects when `StorageChronicleAotCompatibility=true`. A failed project publication writes the partial result and returns the failing `dotnet` exit code. Native AOT success is not an MVP acceptance gate; this script exists to keep compatibility checks reproducible without changing the ordinary release path.

## Tests

The script is validated by `dotnet build StorageChronicle.slnx` and the repository script/static validation. Its publication result is environment-dependent and must not be represented as a release acceptance result unless the command is explicitly executed.
