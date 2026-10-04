# ExternalMediaStore.cs

Implements the `.StorageChronicle` media mirror with writer-specific immutable segments and writer-specific A/B manifests, CRC32C records, SHA-256 segment identity, temporary-to-atomic publication, mount-session links, and branch detection. Every event batch is validated against its first event's volume, mount-session, and optional logical-media identity before a temp file is created. All media I/O uses the injected volume-bound filesystem session; the store does not accept or use path-based fallbacks for the removable volume. Root and writer ownership markers must validate before writes, recovery, or import reads; unmarked roots are not adopted. Unknown root or writer entries, reparse points, malformed paths, and identity mismatch fail closed. Publication accepts only session-issued streams and never replaces existing files. It stores event facts only and never mirrors unrelated PC history.

The public constructor uses a fixed `CommonApplicationData/Storage Chronicle/history/media-recovery-intents` store. The root must be absolute, normalized, local, on a fixed drive, and free of reparse points; directory components are checked before and after creation. The Agent attaches a live write-authorization callback to its mirror store; append, manifest publication, and recovery finalization recheck consent and the current ACL using that store's same pinned volume session immediately before media mutation. A denied or unknown ACL leaves pending canonical events and media entries unchanged. Segment SHA-256 and byte length are computed incrementally from the exact record-length, payload, and CRC bytes written, then the intent is durably saved before the volume-bound rename. Recovery requires a matching PC-local intent bound to the pinned volume identity, writer ID, GUID temp name, length, and SHA-256, followed by CRC/record validation. Missing or malformed intent, corrupt bytes, cancellation, and destination collision retain the temp and never replace an existing `.seg`. The intent-store interface and injection constructor are internal test seams; the only public construction path selects the fixed product store. Tests cover round trips, mixed-media rejection, intent persistence/cancellation/failure, corrupt and forged-temp preservation, collision byte preservation, ownership rejection, mount-point binding, A/B selection, branch detection, mount-session continuity, and live-ACL revocation before flush.

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
