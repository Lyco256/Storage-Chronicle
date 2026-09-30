# StorageChronicle.LiveCorrelationValidator.csproj

## Role

Defines the executable validator that compares observed live event correlation with controlled file-mutation workload evidence.

## Public types and responsibilities

This project file declares no C# public types. It configures a nullable-enabled .NET 10 executable with implicit usings and warnings-as-errors, and references the Storage and Domain projects.

## Invariants

The validator consumes product event/history contracts and must keep its fixture writes bounded to its uniquely owned test root. No file contents are part of the correlation evidence.

## Dependencies

Depends on `StorageChronicle.Storage` and `StorageChronicle.Domain` plus the .NET SDK.

## Failure behavior

Compilation and project-reference failures stop the validator build; runtime validation failures are reported by its executable and its tests.

## Relevant tests

Covered by `tests/StorageChronicle.LiveCorrelationValidator.Tests` and repository quality validation.

## OS constraints

Targets .NET 10. Platform limitations are declared by the executable's runtime checks.
