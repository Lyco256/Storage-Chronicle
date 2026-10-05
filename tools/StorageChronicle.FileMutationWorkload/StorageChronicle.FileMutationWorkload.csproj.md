# StorageChronicle.FileMutationWorkload.csproj

## Role

Defines the executable project for the controlled file-mutation workload used by performance and live-correlation validation.

## Public types and responsibilities

This project file declares no C# public types. It configures an executable targeting .NET 10 with the `StorageChronicle.FileMutationWorkload` root namespace and assembly name, enables XML documentation generation, and grants the Architecture test assembly access to the internal protected-root validator for direct policy tests.

## Invariants

Warnings are treated as errors through repository-wide build properties. The workload must remain explicitly scoped to its caller-provided fixture root; project configuration does not grant permission to mutate arbitrary paths.

## Dependencies

Uses the .NET SDK and repository-wide package/build configuration. Runtime dependencies are declared by the source and solution configuration.

## Failure behavior

Build failures are surfaced by the standard .NET build. Runtime safety and cleanup behavior are implemented and tested in the executable source, not by this project file.

## Relevant tests

Validated by the repository build and the performance/live-correlation test scripts that invoke this executable.

## OS constraints

Targets .NET 10; platform-specific workload support is determined by the executable source and invoked validation lane.
