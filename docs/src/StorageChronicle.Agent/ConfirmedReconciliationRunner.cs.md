# ConfirmedReconciliationRunner.cs

Runs the actual selected-volume reconciliation after the UI has confirmed a pending continuity gap. NTFS uses a pre-scan USN boundary, public lightweight MFT enumeration, candidate comparison against durable canonical state, candidate-only standard metadata reads, and durable Source/Canonical/State updates. Candidate metadata uses `SeBackupPrivilege` and Windows background/low-I/O priority only around the synchronous native call, then restores the thread token before asynchronous work continues; enable/fallback and priority telemetry are aggregated into the result. Non-NTFS uses the production directory snapshot reader and compares the current metadata-only tree with the saved state. Every emitted fact carries a reconciliation run ID, uncertain interval, unknown process attribution, and reconciliation quality. Cancellation, access, media, storage, unexpected execution failures, and an unavailable NTFS post-scan journal boundary append an explicit failed/interrupted gap without deleting already durable history. If the selected volume disappears before enumeration completes, the request's declared filesystem is used only to record that failed gap; no scan is attempted.

## Role

The Agent owns the user-confirmed execution boundary while platform collectors own acquisition and Storage owns durable ordering.

## Inputs and outputs

Input is one selected `PendingReconciliationRequest`; output is a bounded `ReconciliationExecutionSummary` plus durable Source, Canonical, and State updates or an explicit Failed/Interrupted gap. A volume-scoped bounded live-event session remains open through reconciliation appends; committed ordinary events are replayed to state after the scan, and overflow creates a failed gap.

## Public types and responsibilities

`IConfirmedReconciliationRunner` is the Agent-owned execution seam and `ReconciliationExecutionSummary` is the bounded evidence summary. The summary exposes lightweight entry count, candidate count, detailed-query count and ratio, privilege success/failure, ACL fallback, low-priority/I/O-hint telemetry, and elapsed time. The runner coordinates platform acquisition and durable persistence; it does not expose manual UI commands or generate descendant events.

## Invariants

Only the requested volume is scanned. NTFS detailed metadata is requested only for changed candidates; the lightweight MFT pass does not read file contents or hashes. Reconciliation facts remain distinct from LiveUsn/Directory notification facts, and failures remain non-completed outcomes.

## Dependencies

The runner depends on the volume, NTFS, production snapshot, metadata, normalization, health, append storage, and bounded live-event buffer boundaries. Tests cover candidate selection, metadata-query bounds, durable reconciliation quality, deletion, cancellation/failure records, detached-volume failure recording, live-event capture, and role-triggered execution; Windows TestLab supplies the real-I/O acceptance evidence.

## Threading and lifetime

The runner schedules the confirmed operation on a dedicated long-running background task so the IPC request path and ordinary live collector are not the reconciliation worker. Native privilege and priority scopes are created and disposed around each synchronous candidate metadata operation, so thread token state does not cross an await. The live-event capture is bounded and closes before replay.

## Failure behavior

Cancellation, volume-enumeration failures, unavailable media, snapshot continuity gaps, access failures, storage failures, unexpected execution failures, and an unavailable NTFS post-scan journal boundary do not return completed success; the runner appends an explicit gap and exposes the failure status.

## Tests

`tests/StorageChronicle.Agent.Tests/ConfirmedReconciliationRunnerTests.cs` covers candidate-only metadata, unchanged snapshots, durable reconciliation quality, cancellation, volume-enumeration and detached-volume failure recording, post-scan boundary failure, and no-content invariants. `tests/StorageChronicle.Agent.Tests/WindowsReconciliationAcceptanceTests.cs` is the real environment-gated acceptance path; Windows TestLab must execute it to cover real MFT, NTFS, and non-NTFS execution.

## OS constraints

The NTFS route requires Windows public USN/MFT APIs. Non-NTFS uses the platform snapshot boundary and remains portable at the contract level.

## Change-sensitive contracts

Execute is allowed only for a pending selected-volume gap; detailed metadata is candidate-only; facts are durable in Source → Canonical → flush → State order; no contents, hashes, history deletion, or synthetic descendants are allowed.
