# `MediaMirrorApprovalWindow.cs`

## Role

Displays the exact external-media mirror scope awaiting local-user approval. It explains possible existing-history read/import separately from future media-only appends, identifies the PC, medium, volume, filesystem, and dedicated root, and describes ACL protection accurately.

## Public types

- `MediaMirrorApprovalWindow` renders one Agent-issued `PendingMediaMirrorApproval` and returns an explicit approve/decline result.

## Inputs and outputs

Input is a bounded IPC disclosure containing no file contents. Output is only a boolean decision; closing the window without choosing returns no decision.

## Dependencies

Uses Avalonia controls and the shared runtime IPC contract. It performs no filesystem, history, settings, import, or media I/O.

## Invariants

- NTFS ACL verified and NTFS ACL unavailable are shown separately.
- FAT/exFAT/other filesystems are explicitly not described as ACL-protected.
- Unknown protection classification disables approval.
- Approval is scoped to the exact request and both requested effects are visibly disclosed.
- The window never authorizes or performs the underlying operation itself.

## Failure behavior

Missing required effect flags or unknown ACL evidence disables the approval button. Cancel or window close never returns approval.

## Threading and lifetime

The modal is owned by the desktop window. The caller owns the request lifetime, and closing the view without an explicit choice returns no approval.

## Tests

The window is compiled by the desktop UI build. Runtime contract behavior is covered by `tests/StorageChronicle.Agent.Tests/IpcProtocolTests.cs`; the live Agent persistence/action gate remains a separate integration requirement.

## OS constraints

The view is platform-neutral Avalonia UI; filesystem and volume evidence are acquired by the Agent/platform boundary.

## Change-sensitive contracts

Consent wording, effect scope, filesystem/ACL disclosure, and the meaning of explicit approval are safety-sensitive UI contracts.
