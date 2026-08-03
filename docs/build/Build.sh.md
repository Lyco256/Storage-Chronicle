# Build.sh

## Role

Builds the solution through the same root entry point as `build/Build.ps1` on Unix-like hosts.

## Public types and responsibilities

The script exposes the build command and an optional `--no-restore` switch; it does not alter source or requirement files.

## Inputs and outputs

It reads the repository solution and writes only normal SDK outputs and build diagnostics under ignored artifact directories.

## Dependencies

Requires a compatible `dotnet` SDK and the repository solution.

## Invariants

The command returns the SDK exit code and never converts a failed build into success.

## Threading and lifetime

The SDK process is foregrounded and owns the command lifetime.

## Failure behavior

Missing SDK, restore failure, compiler errors, or warnings-as-errors produce a non-zero exit code.

## Tests

Covered by the root build gate and the equivalent PowerShell script.

## OS constraints

This is a Unix-like wrapper; Windows-targeted projects still require a Windows acceptance host for runtime behavior.

## Change-sensitive contracts

The solution path, `--no-restore` behavior, and exit-code propagation are compatibility-sensitive.
