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

- The initial disclosure distinguishes verified, unavailable, and unknown ACL evidence. NTFS approval is accepted only after the Agent rechecks the ACL against the authenticated approving user's SID; the UI itself does not claim the pre-click scan is verified.
- FAT/exFAT/other filesystems are explicitly not described as ACL-protected.
- Unknown protection classification disables approval.
- Approval is scoped to the exact request and describes existing-history import separately from future append in explicit prose, not raw booleans. When the root does not exist, the UI says no old events will be imported and explains that the new dedicated root is created only after approval rather than presenting the internal pending-root sentinel as an identity.
- The window never authorizes or performs the underlying operation itself.

## Failure behavior

Missing required effect flags or unknown protection classification disables the approval button. NTFS approval is rejected by the Agent if the authenticated SID cannot be established or the handle-bound ACL scan fails. Cancel or window close never returns approval.

## Threading and lifetime

The modal is owned by the desktop window. The caller owns the request lifetime, and closing the view without an explicit choice returns no approval.

## Tests

The window is compiled by the desktop UI build. Runtime contract behavior is covered by `tests/StorageChronicle.Agent.Tests/IpcProtocolTests.cs`; the live Agent persistence/action gate remains a separate integration requirement.

## OS constraints

The view is platform-neutral Avalonia UI; filesystem and volume evidence are acquired by the Agent/platform boundary.

## Change-sensitive contracts

Consent wording, effect scope, filesystem/ACL disclosure, and the meaning of explicit approval are safety-sensitive UI contracts.
