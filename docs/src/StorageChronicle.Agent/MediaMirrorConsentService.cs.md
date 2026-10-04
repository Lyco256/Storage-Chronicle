# MediaMirrorConsentService.cs

## Role

`MediaMirrorConsentService` is the Agent-owned gate for first-time use of an external-media history mirror. It binds approval to the local PC identity, logical media ID, enumerated volume identity, filesystem classification, the handle-derived identity of an existing `.StorageChronicle` root, and—on NTFS—the authenticated approving user's SID and a fingerprint of verified ACL evidence.

## Public types and responsibilities

The public `MediaMirrorConsentService` queues a bounded `PendingMediaMirrorApproval`, awaits the local interactive user's explicit decision, re-enumerates the volume, rechecks the root identity and ACL through the same pinned filesystem session used for the operation, and persists a narrowly scoped grant through `AgentSettingsService`. The authenticated SID comes from the named-pipe client identity, never from the decision payload. Existing-history read/import permission is recorded separately from permission for future appends. If an approved NTFS medium has no product root yet, the service creates only that new root after the explicit approval, then requires a complete parent/tree ACL scan before it persists consent or permits import/mirror access. If that scan fails, the empty product-owned root may remain, but no grant, history import, or append is authorized.

## Dependencies

The service uses the canonical settings contract/store, Agent health state, volume enumeration, the volume-bound media filesystem, the pure consent policy, and the settings service. It does not access media file contents.

## Inputs and outputs

Inputs are the enumerated media descriptor, configured dedicated root, and an authenticated local-user decision. Output is a boolean authorization result; the service emits a pending UI disclosure through `AgentHealthState` and persists only a validated consent binding.

## Invariants

- A legacy `MediaMirrors` setting is not consent.
- No pending or declined request authorizes media reads, import, recovery, or writes.
- Approval applies only when the live media and root identities still match the disclosed request.
- Non-NTFS filesystems are classified as not providing NTFS ACL protection. NTFS requires a verified handle-bound product-tree ACL scan; the exact approving user's SID may have the narrowly required history create/append rights. Broad group write/delete rights, delete/rename rights, parent `DELETE_CHILD`, unknown ACEs, and incomplete scans must fail closed in the Windows implementation.
- NTFS grants require a valid exact SID and ACL descriptor fingerprint; the ACL is re-inspected against the saved SID before mirror/import use. Any mismatch, missing SID, or unavailable evidence denies access. A new root's post-creation check must pass before any consent is persisted.
- The ordinary settings update route cannot create, alter, or remove consent entries; only the explicit verified grant path may add a grant.
- Consent persistence is PC-local and records the import capability separately from future append capability.

## Failure behavior

Unknown or changed identity, role-classification failures, invalid settings, cancellation, persistence failure, and volume removal fail closed. Pending requests are resolved and removed when decisions complete or the awaiting collector is cancelled.

## Threading and lifetime

Approval waits are asynchronous and cancellation-aware. The owning collector controls the wait token; pending requests are bounded by `AgentHealthState` and removed after completion or cancellation.

## Relevant tests

`tests/StorageChronicle.Agent.Tests/MediaMirrorConsentServiceTests.cs` covers approval persistence, authenticated-SID requirements, new-root creation followed by ACL verification, refusal when the post-creation scan is unsafe, identity change, and ACL-fingerprint change. Collector tests cover legacy settings without consent. Tests use only run-owned temporary fixtures and do not constitute Windows identity or ACL evidence.

## OS constraints

The policy is platform-neutral, while production ACL evidence comes from the Windows volume-bound filesystem implementation. The contract's default ACL result is `Unknown`, so an unimplemented or unsupported scanner denies NTFS consent. Physical acceptance remains closed until the Windows scanner and its dedicated audit are complete.

## Change-sensitive contracts

Consent fields, the distinction between existing-history import and future append, request IDs, and the identity binding are security-sensitive persistence and IPC contracts. Changes require compatibility, refusal, cancellation, and recovery tests.
# MediaMirrorConsentService.cs

Coordinates explicit per-PC/per-media authorization for external-media history import and append. A pending approval binds the authenticated approver SID, PC identity, logical media identity, live volume identity, dedicated product-root file identity, filesystem, and (on NTFS) the verified ACL descriptor fingerprint. Approval is revalidated against the currently enumerated media before settings persistence. NTFS write rights are limited to the exact approver SID and the product-history operations defined by Requirement 37; unknown or changed ACLs fail closed. First-use tree creation passes an explicit callback bound to the already approved, pinned volume; after creation, the complete tree ACL inspection remains required before grant persistence or history import/append.

If the product root does not exist, an explicit approval may initialize the new `.StorageChronicle` product tree, writer directory, and ownership markers at the selected mount root. The complete root/tree ACL is inspected after initialization and before consent persistence, import, or event-segment append. If that check fails, the empty product-owned setup tree may remain; existing media user data is not touched and the approval is not persisted. FAT/exFAT consent discloses that the filesystem does not provide the same ACL assurance.

`HasGrant` is also the live gate used by the mirror coordinator immediately before each append, manifest publication, and interrupted-write finalization, against the store's same pinned volume session. It binds to the saved consent and exact ACL fingerprint, so changed identity/ACL or incomplete inspection denies mutation. A denied flush leaves the media unchanged; canonical facts remain in PC-local history even if a removal-time mirror session is then closed. `CanReadExistingHistory` separately requires the saved import choice. Tests cover approval/cancellation, stale identity, ACL mismatch, persistence failure, and revocation between registration and flush.
