# `MediaMirrorConsentPolicy.cs`

## Role

Defines the platform-neutral, deterministic policy that binds an explicit external-media consent to current PC and media identity evidence. It performs no filesystem, settings, UI, or persistence operations.

## Public types

- `MediaConsentFileSystem` distinguishes NTFS, FAT, FAT32, exFAT, other known filesystems, and unknown classification.
- `MediaConsentAclProtection` distinguishes verified NTFS ACL protection, unavailable NTFS ACL evidence, filesystems that do not provide the asserted NTFS ACL protection, and unknown evidence.
- `MediaMirrorConsentBinding` is an opaque evidence value containing PC identity, logical media ID, live volume identity, dedicated media-root identity, filesystem, ACL/protection classification, exact approving-user SID, and ACL descriptor fingerprint. It is a policy value, not a serialized settings model.
- `MediaMirrorConsentUserDecision`, `MediaMirrorConsentStatus`, and `MediaMirrorConsentReason` represent the explicit prompt decision and fail-closed outcomes.
- `MediaMirrorConsentEvaluation` returns the decision and includes a binding for persistence only after explicit acceptance.
- `MediaMirrorConsentPolicy.Evaluate` is the single policy entry point.

## Inputs and outputs

Inputs are freshly acquired current identity evidence, an optional previously persisted binding supplied by the caller, and the result of an explicit consent prompt if one was shown. The output is an authorization status, reason, and (only after explicit acceptance) the exact current evidence value for the shared persistence owner. No event data, file contents, or content hashes are read or returned.

## Invariants

- Every identity and both classifications must be known before current evidence can be accepted.
- All four identity strings compare exactly with ordinal semantics; any changed value forces reapproval.
- An NTFS request may await approval while ACL evidence is unavailable, but it cannot be persisted or authorized until `NtfsAclVerified`, a syntactically valid approving-user SID, and a 64-hex SHA-256 descriptor fingerprint are present. An unavailable/legacy NTFS consent never authorizes a grant.
- FAT, FAT32, exFAT, and other non-NTFS filesystems require `NotProvidedByFileSystem`; they can never be classified as NTFS-ACL-protected.
- Missing, legacy, and incomplete saved consent never authorize use. They require explicit approval.
- Cancellation and invalid current evidence never authorize use, even if a decision value otherwise requests acceptance.
- A replacement binding is returned only after an explicit acceptance. The caller must persist it through the canonical Settings/Agent layer before import or mirror writes.
- An exact saved binding, including the same approving-user SID and ACL fingerprint, authorizes current evidence; any change forces reapproval and explicit cancellation overrides it.
- The policy does not establish that supplied identities are authentic. The caller must derive live volume/root identities and ACL evidence through its audited acquisition boundary; a mount-point string alone is insufficient.

## Dependencies and failure behavior

The implementation uses only .NET base types and has no platform-specific dependency. Null arguments throw `ArgumentNullException`; unknown, malformed, mismatched, missing, legacy, and cancelled states return explicit outcomes rather than throwing or authorizing. The policy creates no files, changes no settings, and does not perform import or mirror I/O.

## Threading and lifetime

The evaluator is synchronous, stateless, and safe to call without synchronization. Callers own the evidence lifetime and must reevaluate after every relevant media, volume, root, PC, filesystem, or protection change.

## OS constraints

The policy is OS-neutral. Windows-specific discovery of volume identity, owned-root identity, filesystem, and ACL state belongs in the platform acquisition layer and must be independently validated before its values are passed here.

## Tests

`tests/StorageChronicle.ExternalMedia.Tests/MediaMirrorConsentPolicyTests.cs` covers exact match; missing/legacy and incomplete bindings; identity, approving SID, ACL fingerprint, and classification changes; accepted replacement evidence; unavailable NTFS ACL refusal; malformed approver/fingerprint; FAT/FAT32/exFAT/other ACL misclassification; unknown current evidence; cancellation; and invalid decision values.

## Change-sensitive contracts

The binding fields, exact comparison semantics, classification enums, fail-closed statuses, and `BindingToPersist` availability are security-sensitive policy contracts. Changes require updating this mirror, tests, and the shared Settings/Agent handoff together.
