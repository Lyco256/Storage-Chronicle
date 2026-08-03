# Test-Fast.ps1

Runs the full solution test command used for the fast deterministic gate.
## Role

Builds and runs every test project while excluding only the explicit Windows privileged acceptance project.

## Public types and responsibilities

The script exposes `-NoRestore`, enumerates `tests/**/*.Tests.csproj`, excludes the environment-bound privileged project, builds each project, resolves its emitted MTP test module, and runs that module synchronously so Microsoft Testing Platform child processes are fully joined.

## Inputs and outputs

Input is the repository configuration; output is the test runner exit code and console diagnostics.

## Dependencies

Depends on the .NET SDK and the Microsoft Testing Platform runner. Running the emitted module avoids a .NET 10 project-level `dotnet test` path that can report a false zero-test result.

## Invariants

The privileged integration project is never silently converted to a zero-test success in the normal gate.

## Threading and lifetime

The script owns each foreground test invocation for its turn and propagates the first non-zero exit code; no detached test child is treated as a completed project.

## Failure behavior

Build, discovery, restore, or test failures stop the script and return the failing exit code.

## Tests

Validated by `tests/StorageChronicle.Integration.Tests/` and the full non-privileged solution test run.

## OS constraints

Runs on supported .NET hosts; Windows-only acceptance is isolated under the `WindowsPrivileged` trait.

## Change-sensitive contracts

The project exclusion and exit-code propagation are part of the normal-vs-privileged acceptance contract.
