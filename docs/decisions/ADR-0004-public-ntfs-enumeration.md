# ADR-0004: Public NTFS enumeration APIs

## Status

Accepted.

## Decision

Use documented USN control codes and public MFT enumeration APIs for NTFS continuity and reconciliation. Raw `$MFT` sector parsing is prohibited.

## Rationale

Public boundaries are supportable and allow journal identity, truncation, access, and media-removal gaps to remain explicit.

## Consequences

The collector never creates, deletes, resizes, or extends a USN journal and must distinguish unsupported, absent, and inaccessible states.

## Verification

`tests/StorageChronicle.Platform.Windows.Ntfs.Tests/` and the capability-gated tests in `tests/StorageChronicle.Platform.Windows.Integration.Tests/`.
