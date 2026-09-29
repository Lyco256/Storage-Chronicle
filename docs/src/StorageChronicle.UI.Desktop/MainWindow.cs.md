# MainWindow.cs

## Role

Top-level shell for Event Stack, Diff View, Agent health, and the modal settings entry point.

## Public types and responsibilities

`MainWindow` composes Avalonia controls and routes settings through `AgentPipeSettingsGateway` and `SettingsDialogWindow`. It owns no storage or collector logic.

## Inputs and outputs

The constructor creates a local Agent projection client. The Settings button opens a modal child; transport failures before construction update the shell title.

## Dependencies

Avalonia, `StorageChronicle.UI.EventStack`, `StorageChronicle.UI.DiffView`, `StorageChronicle.UI.Settings`, and `StorageChronicle.UI.Shared`.

## Invariants

The desktop process never opens machine or user settings files and never replaces unavailable Agent data with fabricated history.

## Threading and lifetime

Button handlers await IPC and modal operations on the UI synchronization context. Settings apply cancellation is owned by the modal dialog.

## Failure behavior

Named-pipe timeout, authorization, invalid-payload, and unavailable-Agent errors remain visible without terminating the shell.

## Tests

`tests/StorageChronicle.UI.Headless.Tests` verifies shell startup. Settings validation, cancellation, recovery, and apply failures are covered by `tests/StorageChronicle.UI.Settings.Tests`.

## OS constraints

Machine authorization and persistence remain Agent-owned and Windows-specific; the shell uses the platform-neutral IPC boundary.

## Change-sensitive contracts

Settings request/response types remain versioned under `StorageChronicle.Contracts.Runtime`.

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
