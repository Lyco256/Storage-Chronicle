# MediaMirrorConsentService.cs

## Role

`MediaMirrorConsentService` is the Agent-owned gate for first-time use of an external-media history mirror. It binds approval to the local PC identity, logical media ID, enumerated volume identity, filesystem classification, and the handle-derived identity of an existing `.StorageChronicle` root.

## Public types and responsibilities

The public `MediaMirrorConsentService` queues a bounded `PendingMediaMirrorApproval`, awaits the local interactive user's explicit decision, re-enumerates the volume, rechecks the root identity, and persists a narrowly scoped grant through `AgentSettingsService`. Existing-history read/import permission is recorded separately from permission for future appends. A newly created root is created only after approval and receives no existing-history import permission.

## Dependencies

The service uses the canonical settings contract/store, Agent health state, volume enumeration, the volume-bound media filesystem, the pure consent policy, and the settings service. It does not access media file contents.

## Inputs and outputs

Inputs are the enumerated media descriptor, configured dedicated root, and an authenticated local-user decision. Output is a boolean authorization result; the service emits a pending UI disclosure through `AgentHealthState` and persists only a validated consent binding.

## Invariants

- A legacy `MediaMirrors` setting is not consent.
- No pending or declined request authorizes media reads, import, recovery, or writes.
- Approval applies only when the live media and root identities still match the disclosed request.
- Non-NTFS filesystems are classified as not providing NTFS ACL protection; NTFS ACL evidence is currently reported unavailable until a verified ACL inspection is implemented.
- Consent persistence is PC-local and records the import capability separately from future append capability.

## Failure behavior

Unknown or changed identity, role-classification failures, invalid settings, cancellation, persistence failure, and volume removal fail closed. Pending requests are resolved and removed when decisions complete or the awaiting collector is cancelled.

## Threading and lifetime

Approval waits are asynchronous and cancellation-aware. The owning collector controls the wait token; pending requests are bounded by `AgentHealthState` and removed after completion or cancellation.

## Relevant tests

`tests/StorageChronicle.Agent.Tests/MediaMirrorConsentServiceTests.cs` covers approval persistence, new-root creation, refusal, and identity change. Collector tests cover legacy settings without consent. Tests use only run-owned temporary fixtures and do not constitute Windows identity or ACL evidence.

## OS constraints

The policy is platform-neutral, while the production filesystem identity evidence comes from the Windows volume-bound filesystem implementation. The current service does not yet perform an NTFS ACL inspection, so the Windows physical acceptance gate remains closed.

## Change-sensitive contracts

Consent fields, the distinction between existing-history import and future append, request IDs, and the identity binding are security-sensitive persistence and IPC contracts. Changes require compatibility, refusal, cancellation, and recovery tests.
