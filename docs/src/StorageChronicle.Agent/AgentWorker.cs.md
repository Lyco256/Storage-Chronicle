# AgentWorker.cs

Adapts the multi-collector pipeline to the .NET hosted-service lifecycle. Before the first collection pass it rehydrates unresolved continuity prompts from canonical history, then forwards collector failures, source continuity, and bounded queue depth to health IPC, catches durable pipeline failures as quality state, retries recoverable stopped storage through `TryResumeAsync` and then retries retained process-lifecycle facts, disposes event-driven Windows sources on shutdown, drains the process-lifecycle persistence queue, and only then stops durable storage. It has no UI and is intended for LocalSystem deployment with service recovery settings.

## Role

This mirror documents the source boundary for this file and explains how it participates in Storage Chronicle. The worker wires the singleton bounded reconciliation live-event buffer to the pipeline's post-commit source notification so reconciliation never relies on a global monitoring stop. The optional process-lifecycle state is drained after collectors stop and before the storage engine stops, preserving queued ProcessStop facts during graceful shutdown.

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

Validated by `tests/StorageChronicle.Agent.Tests/AgentWorkerTests.cs` for shutdown drain ordering and by the Agent projection tests for process-exit replay.

## OS constraints

Platform-neutral behavior remains portable; Windows-only APIs are isolated in the Windows platform projects.

## Change-sensitive contracts

Public names, serialized fields, persistence boundaries, and the mirrored path are compatibility-sensitive contracts.
