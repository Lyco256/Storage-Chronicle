# RuntimeMessages.cs

Defines source-generated page, reconciliation, health, settings, and bounded clipboard messages used by the local Agent/UI/Session boundary. Health includes pending continuity decisions with filesystem and optional last-continuous boundary facts, plus the bounded pipeline queue depth for monitoring. Diff responses include a separately paged list of metadata-only Activity Group summaries; the request carries ascending/newest-first direction so the Agent sorts before selecting a page. A selected frame's event timeline is retrieved through its own bounded request/response. The Agent emits frames for Live/Replay modes only, derives expiry from configured pane timeout, and may report a close observed from transient ProcessStop state. Clipboard and frame messages contain only path metadata and generation/quality facts; no file or clipboard content and no hashes are represented.

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

## External-media approval messages

`PendingMediaMirrorApproval` is the bounded IPC disclosure for a single mounted medium and configured mirror root. It binds the request to the local PC identity, logical media identity, live volume identity, filesystem, and ACL-disclosure classification. `MediaMirrorAclDisclosure.Unknown` is fail-closed; non-NTFS filesystems are represented as lacking Windows ACL protection, never as ACL-verified. `MediaMirrorApprovalDecision` carries only the request ID and explicit approve/decline choice; the Agent must resolve that ID against its live pending request before acting. `AgentHealth.PendingMediaMirrorApprovals` is optional for protocol compatibility.
