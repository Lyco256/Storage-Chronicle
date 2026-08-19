# ConfirmedReconciliationRunner.cs

Runs the actual selected-volume reconciliation after the UI has confirmed a pending continuity gap. NTFS uses a pre-scan USN boundary, public lightweight MFT enumeration, candidate comparison against durable canonical state, candidate-only standard metadata reads, and durable Source/Canonical/State updates. Candidate metadata uses `SeBackupPrivilege` and Windows background/low-I/O priority only around the synchronous native call, then restores the thread token before asynchronous work continues; enable/fallback and priority telemetry are aggregated into the result. Non-NTFS uses the production directory snapshot reader and compares the current metadata-only tree with the saved state. Every emitted fact carries a reconciliation run ID, uncertain interval, unknown process attribution, and reconciliation quality. Cancellation, access, media, or storage failures append an explicit failed/interrupted gap without deleting already durable history.

## Role

The Agent owns the user-confirmed execution boundary while platform collectors own acquisition and Storage owns durable ordering.

## Inputs and outputs

Input is one selected `PendingReconciliationRequest`; output is a bounded `ReconciliationExecutionSummary` plus durable Source, Canonical, and State updates or an explicit Failed/Interrupted gap. A volume-scoped bounded live-event session remains open through reconciliation appends; committed ordinary events are replayed to state after the scan, and overflow creates a failed gap.

## Public types and responsibilities

`IConfirmedReconciliationRunner` is the Agent-owned execution seam and `ReconciliationExecutionSummary` is the bounded evidence summary. The runner coordinates platform acquisition and durable persistence; it does not expose manual UI commands or generate descendant events.

## Invariants

Only the requested volume is scanned. NTFS detailed metadata is requested only for changed candidates; the lightweight MFT pass does not read file contents or hashes. Reconciliation facts remain distinct from LiveUsn/Directory notification facts, and failures remain non-completed outcomes.

## Dependencies

The runner depends on the volume, NTFS, production snapshot, metadata, normalization, health, append storage, and bounded live-event buffer boundaries. Tests cover candidate selection, metadata-query bounds, durable reconciliation quality, deletion, cancellation/failure records, live-event capture, and role-triggered execution; Windows TestLab supplies the real-I/O acceptance evidence.

## Threading and lifetime

The runner is asynchronous and bounded by the caller cancellation token. Native privilege and priority scopes are created and disposed around each synchronous candidate metadata operation, so thread token state does not cross an await. The live-event capture is bounded and closes before replay.

## Failure behavior

Cancellation, unavailable media, snapshot continuity gaps, access failures, and storage failures do not return completed success; the runner appends an explicit gap and exposes the failure status.

## Tests

`tests/StorageChronicle.Agent.Tests/ConfirmedReconciliationRunnerTests.cs` covers candidate-only metadata, unchanged snapshots, durable reconciliation quality, cancellation, and no-content invariants. Windows TestLab covers real MFT, NTFS, and non-NTFS execution.

## OS constraints

The NTFS route requires Windows public USN/MFT APIs. Non-NTFS uses the platform snapshot boundary and remains portable at the contract level.

## Change-sensitive contracts

Execute is allowed only for a pending selected-volume gap; detailed metadata is candidate-only; facts are durable in Source → Canonical → flush → State order; no contents, hashes, history deletion, or synthetic descendants are allowed.
