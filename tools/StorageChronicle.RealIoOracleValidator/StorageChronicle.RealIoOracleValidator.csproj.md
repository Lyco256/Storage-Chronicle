# StorageChronicle.RealIoOracleValidator.csproj

## Role

Defines the executable that checks persisted real-I/O observations against the validator's expected oracle.

## Public types and responsibilities

This project file declares no C# public types. It configures a .NET 10 executable with XML documentation generation and references Storage and Domain.

## Invariants

The oracle validates recorded metadata/event quality, not file contents or content hashes. Any fixture mutation must remain within the run-owned fixture documented by the executable and test harness.

## Dependencies

Depends on `StorageChronicle.Domain`, `StorageChronicle.Storage`, and the .NET SDK.

## Failure behavior

Build/reference errors fail the validator build; unmet oracle conditions are surfaced as validation failures by the program and tests.

## Relevant tests

Covered by `tests/StorageChronicle.RealIoOracleValidator.Tests` and the repository quality suite.

## OS constraints

Targets .NET 10. Host- and filesystem-specific checks are enforced by the executable rather than this project file.
