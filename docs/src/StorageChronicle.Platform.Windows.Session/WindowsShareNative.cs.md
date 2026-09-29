# WindowsShareNative.cs

## Role

Contains the thin native adapters for `NetShareEnum` level 2 and `RegNotifyChangeKeyValue` on the LanmanServer Shares registry key.

## Native boundaries

NetShareEnum maps share name, local path, share type, remark, and permission mask into the shared `ShareDescriptor`. Registry notification uses an asynchronous event handle and re-registers after each notification. No remote enumeration or read-access history is requested.

## Failure and recovery

Access denied and native enumeration errors are preserved for the snapshot caller. Missing registry notification support exposes `IsAvailable=false`, allowing the source to activate the 30-second fallback. Disposal closes the registry key and event handle.

## Tests

The source tests cover injected notifier behavior and fallback quality. Native P/Invoke structure and privileged SMB snapshot behavior belong to the Windows privileged test gate.

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
