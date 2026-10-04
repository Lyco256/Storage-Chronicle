# WindowsPcIdentity.cs

## Role

Provides the Agent's stable Windows-installation identifier used to scope external-media consent to one PC installation.

## Public types and responsibilities

The internal `WindowsPcIdentity` helper reads `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid` with a read-only registry handle and canonicalizes it as a namespaced GUID. It does not create or modify registry values.

## Inputs and outputs

Input is the OS-provided MachineGuid string; output is an opaque stable consent identity. Missing, malformed, or empty GUID values throw and prevent the consent service from silently falling back to a hostname.

## Dependencies

Uses `Microsoft.Win32.Registry` and the .NET GUID parser. The helper is used only by the Windows Agent composition root.

## Invariants

Machine identity is not derived from drive letters, mount points, volume labels, or mutable host name. The MachineGuid is treated as installation identity evidence, not cryptographic proof against an administrator or cloned Windows image.

## Threading and lifetime

The registry key is opened read-only, read once during Agent dependency composition, and disposed immediately.

## Failure behavior

Registry access failure or invalid identity fails startup/consent composition closed; no writable fallback is attempted.

## Relevant tests

`tests/StorageChronicle.Agent.Tests/IpcProtocolTests.cs` verifies canonicalization and rejects missing, malformed, and empty identities without accessing the machine registry.

## OS constraints

This helper is Windows-only and belongs to the Windows Agent. It does not make registry writes.

## Change-sensitive contracts

The `windows-machine-guid:` namespace is persisted in media-consent bindings. Changing it invalidates prior grants and must trigger explicit re-approval.
