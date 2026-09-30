# AgentEventStackProjection.cs

`AgentEventStackProjection` adapts the bounded named-pipe projection contract to the Event Stack UI contract. It forwards mode, page, sort, and semantic filter state, preserves page boundaries, and maps selected-row details without allowing the UI to read the Agent log directly.

Grouped rows and their bounded expansion children are expected to be returned by the Agent in the same page. The adapter retains the source quality, process attribution, reconciliation, and gap markers needed by the detail panel. Its tests belong to the Event Stack and headless UI test projects and cover mode switching, filtering, paging, expansion, and disconnected-agent errors.

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
