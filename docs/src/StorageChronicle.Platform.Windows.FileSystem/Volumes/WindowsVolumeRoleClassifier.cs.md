# WindowsVolumeRoleClassifier.cs

## Role

Pure Windows-platform classifier mapping one enumerated volume to exactly one read-only `NativePartitionRoleRecord`. It is kept separate from the WMI transport so deterministic tests can exercise role recognition and all fail-closed cases without accessing the host's disks.

## Public types and responsibilities

`WindowsVolumeRoleClassifier.Classify` returns the shared `ProtectedVolumeRoles` flags and a completeness bit. It recognizes GPT EFI, Microsoft Basic Data, Microsoft Recovery, and a conservative list of common MBR data types plus MBR recovery type `0x27`. Unknown styles/types, absent role properties, missing/duplicate path matches, or failed/partial queries add `Unknown` and return incomplete. It does not claim that the external-media write path is bound to this identity.

## Dependencies and invariants

Depends on `NativePartitionRoleRecord` and the single shared `ProtectedVolumeRoles` contract. It performs no I/O, does not invoke storage mutation APIs, and never infers an unrecognized partition to be safe. GPT/MBR matching requires the volume GUID or one current mount path to match exactly, case-insensitively after trailing-separator normalization.

## Inputs and outputs

Inputs are the enumerated volume GUID, current mount paths, the complete partition query result, and an explicit query-success flag. Output is a role bitmask plus a completeness flag. No file content or file-content hash is inspected or returned.

## Threading and lifetime

The classifier is synchronous, stateless, and safe to call concurrently. The caller owns the lifetime of the partition-query records.

## Failure behavior and tests

Malformed or unsupported partition metadata fails closed as `Unknown`. `tests/StorageChronicle.Platform.Windows.FileSystem.Tests/VolumeAndMediaTests.cs` covers EFI, Recovery, System+Boot, unknown GPT type, no match, ambiguous match, and failed query. Actual WMI availability and host role agreement remain unverified until the later, separately gated Windows acceptance stage.

## OS constraints

This classifier is platform-specific and consumes facts produced by Windows Storage Management. It remains pure and testable on non-privileged test runs.

## Change-sensitive contracts

The `ProtectedVolumeRoles` flags and the rule that any ambiguity yields `Unknown`/incomplete are security-sensitive contracts. Do not make unknown layouts implicitly eligible for external-media writes.
