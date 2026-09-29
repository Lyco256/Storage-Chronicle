# AgentPipeSettingsGateway.cs

## Role

Adapts the versioned Agent settings named-pipe endpoint to the UI settings gateway. The desktop process does not open machine or user settings files.

## Public types

`AgentPipeSettingsGateway` implements `IAgentSettingsGateway` for machine and user load/apply operations.

## Invariants and dependencies

- Requests use the existing `AgentPipeProjectionClient` transport and `IpcProtocol` envelope; the settings payload itself uses the settings model's web JSON options.
- Settings are serialized as bounded JSON DTO payloads; file contents and file-content hashes are never read.
- Agent-side authorization and validation remain authoritative.

## Failure behavior

Transport, protocol, authorization, validation, and deserialization failures propagate to the settings ViewModel, which renders them as an error instead of success.

## Relevant tests

The gateway is exercised by the desktop headless settings integration tests and the settings ViewModel failure/recovery tests.

## Role

This mirror documents the source boundary for this file and explains how it participates in Storage Chronicle.

## Public types and responsibilities

Public types preserve source facts and the explicitly owned responsibility; UI interpretation and correlation remain outside this boundary.

## Inputs and outputs

Inputs and outputs are the declared contracts of the source file. File contents and file-content hashes are never an input or output.

## Dependencies

Dependencies are limited to the referenced project contracts and platform services shown by the source file.

## Invariants

The source keeps canonical facts distinguishable from reconstructed state and does not synthesize descendant events.

## Threading and lifetime

Callers own cancellation and lifetime; asynchronous work must not outlive the owning pipeline or UI scope.

## Failure behavior

Failure, corruption, cancellation, and recovery remain observable and are not converted into a false successful observation.

## Tests

Validated by tests/StorageChronicle.Integration.Tests and the affected integration tests.

## OS constraints

Platform-neutral behavior remains portable; Windows-only APIs are isolated in the Windows platform projects.

## Change-sensitive contracts

Public names, serialized fields, persistence boundaries, and the mirrored path are compatibility-sensitive contracts.
