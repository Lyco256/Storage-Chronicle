# MediaMirrorConsentService.cs

## Role

`MediaMirrorConsentService` is the Agent-owned gate for first-time use of an external-media history mirror. It binds approval to the local PC identity, logical media ID, enumerated volume identity, filesystem classification, the handle-derived identity of an existing `.StorageChronicle` root, and—on NTFS—the authenticated approving user's SID and a fingerprint of verified ACL evidence.

## Public types and responsibilities

The public `MediaMirrorConsentService` queues a bounded `PendingMediaMirrorApproval`, awaits the local interactive user's explicit decision, re-enumerates the volume, rechecks the root identity and ACL through the same pinned filesystem session used for the operation, and persists a narrowly scoped grant through `AgentSettingsService`. The authenticated SID comes from the named-pipe client identity, never from the decision payload. Existing-history read/import permission is recorded separately from permission for future appends. A newly created root is created only after approval and receives no existing-history import permission.

## Dependencies

The service uses the canonical settings contract/store, Agent health state, volume enumeration, the volume-bound media filesystem, the pure consent policy, and the settings service. It does not access media file contents.

## Inputs and outputs

Inputs are the enumerated media descriptor, configured dedicated root, and an authenticated local-user decision. Output is a boolean authorization result; the service emits a pending UI disclosure through `AgentHealthState` and persists only a validated consent binding.

## Invariants

- A legacy `MediaMirrors` setting is not consent.
- No pending or declined request authorizes media reads, import, recovery, or writes.
- Approval applies only when the live media and root identities still match the disclosed request.
- Non-NTFS filesystems are classified as not providing NTFS ACL protection. NTFS requires a verified handle-bound product-tree ACL scan; the exact approving user's SID may have the narrowly required history create/append rights. Broad group write/delete rights, delete/rename rights, parent `DELETE_CHILD`, unknown ACEs, and incomplete scans must fail closed in the Windows implementation.
- NTFS grants require a valid exact SID and ACL descriptor fingerprint; the ACL is re-inspected against the saved SID before mirror/import use. Any mismatch, missing SID, or unavailable evidence denies access.
- The ordinary settings update route cannot create, alter, or remove consent entries; only the explicit verified grant path may add a grant.
- Consent persistence is PC-local and records the import capability separately from future append capability.

## Failure behavior

Unknown or changed identity, role-classification failures, invalid settings, cancellation, persistence failure, and volume removal fail closed. Pending requests are resolved and removed when decisions complete or the awaiting collector is cancelled.

## Threading and lifetime

Approval waits are asynchronous and cancellation-aware. The owning collector controls the wait token; pending requests are bounded by `AgentHealthState` and removed after completion or cancellation.

## Relevant tests

`tests/StorageChronicle.Agent.Tests/MediaMirrorConsentServiceTests.cs` covers approval persistence, authenticated-SID requirements, new-root creation, refusal, identity change, and ACL-fingerprint change. Collector tests cover legacy settings without consent. Tests use only run-owned temporary fixtures and do not constitute Windows identity or ACL evidence.

## OS constraints

The policy is platform-neutral, while production ACL evidence comes from the Windows volume-bound filesystem implementation. The contract's default ACL result is `Unknown`, so an unimplemented or unsupported scanner denies NTFS consent. Physical acceptance remains closed until the Windows scanner and its dedicated audit are complete.

## Change-sensitive contracts

Consent fields, the distinction between existing-history import and future append, request IDs, and the identity binding are security-sensitive persistence and IPC contracts. Changes require compatibility, refusal, cancellation, and recovery tests.
