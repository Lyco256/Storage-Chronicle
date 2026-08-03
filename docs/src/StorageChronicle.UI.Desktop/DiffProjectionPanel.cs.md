# DiffProjectionPanel.cs

## Role

Hosts the shared Diff View model in the Avalonia desktop shell. The panel exposes Tree and Explorer presentations, the four time modes, the eight bounded Explorer layouts, explicit selected-row Tree expansion/collapse through the lazy model, navigation, replay, split-pane state, and guarded current-item opening.

## Public types

`DiffProjectionPanel` is the desktop `UserControl`. `AgentDiffProjectionSource` is an internal adapter from the versioned Agent rich projection response to the UI-neutral Diff View projection.

## Invariants and dependencies

- Projection data comes from `IProjectionService` or the Agent IPC client; the panel never reads monitored file contents.
- Tree descendants are loaded through the shared lazy Tree model.
- Explorer rendering is bounded to the first 250 rows; the complete row set remains in the ViewModel for navigation and tests.
- Deleted, virtual, and unknown-location rows cannot be opened through the operating-system Explorer adapter.

## Failure behavior

Agent transport, timeout, and authorization failures remain visible in the status area. Replay and split-pane controls only alter UI state and do not stop Agent recording.

## Relevant tests

`tests/StorageChronicle.UI.Headless.Tests` starts the shell under Avalonia headless mode. `tests/StorageChronicle.UI.DiffView.Tests` covers all time modes, layouts, lazy Tree behavior, navigation, split panes, replay, and Explorer eligibility.

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
