# AgentInternalsVisibleTo.cs

Grants the Agent test assembly access to internal Agent-only test seams. It does not widen the production API or grant access to other assemblies. The relevant seam allows integration tests to redirect ledger persistence into a run-owned isolated fixture; production construction always uses the canonical local product-data directory.

## Role

This assembly metadata supports safe, isolated integration testing without adding a public path override.

## Invariants

Only `StorageChronicle.Agent.Tests` receives friend access. The product's public collector API does not accept a caller-selected ledger root.

## Public types and responsibilities

This file declares assembly-level test visibility metadata and adds no public runtime type.

## Inputs and outputs

The compiler consumes the friend assembly name and applies it to the Agent assembly; there are no runtime inputs or outputs.

## Dependencies

Depends only on `System.Runtime.CompilerServices` assembly metadata.

## Threading and lifetime

The declaration is static assembly metadata and has no runtime lifetime or threading behavior.

## Failure behavior

Compilation fails if the friend assembly name changes without updating this declaration and the corresponding test project.

## Tests

Validated by `tests/StorageChronicle.Agent.Tests/ExternalMediaCollectorTests.cs`.

## OS constraints

The friend-assembly declaration is platform-neutral and does not use Windows APIs.

## Change-sensitive contracts

The friend assembly name must match the test project's assembly name; this declaration must not be widened to product or unrelated assemblies.
