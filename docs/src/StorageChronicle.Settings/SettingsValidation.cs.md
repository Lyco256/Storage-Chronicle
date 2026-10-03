# SettingsValidation.cs

## Role

Validates machine and user settings before persistence or runtime application. It owns value ranges, absolute-path syntax, and the non-overlap rule between monitored roots and local history storage.

## Public types

- `SettingsValidator` validates machine/user settings and exposes throwing `EnsureValid` helpers.
- `SettingsValidationException` carries the structured `SettingsValidationResult` when validation fails.

## Invariants

- History storage must be a local rooted path and cannot equal, contain, or be contained by a monitored path. This prevents durable product writes and recovery deletes from entering a monitored data tree.
- Path overlap uses canonical full paths and the platform's path case semantics. This check does not resolve reparse points or establish filesystem ownership; runtime path resolution remains a separate safety gate.
- Invalid paths and out-of-range values remain explicit validation errors; they are not silently normalized into accepted settings.
- Media consent entries require a unique PC/logical-media key, nonempty live volume and dedicated-root identities, an approval timestamp, and a known filesystem/protection pair. NTFS accepts only NTFS-specific classifications; known non-NTFS formats accept only `NotProvidedByFileSystem`; unknown formats and unknown protection evidence are rejected.

## Dependencies

Uses `System.IO` for rooted-path normalization and the settings models/results in this project. It contains no Windows-only API so settings contracts remain platform-neutral.

## Failure behavior

`Validate` returns every detected error. Path-normalization failures do not escape as success; malformed syntax is reported by the ordinary path validator. `EnsureValid` throws `SettingsValidationException` with the structured result.

## Tests

`tests/StorageChronicle.Settings.Tests/SettingsTests.cs` covers path syntax, UNC rejection for history, overlapping roots in both directions, case-insensitive Windows duplicates, and valid disjoint machine settings in addition to existing settings validation.

## Scope limitations

The lexical path check does not prove that junctions, symlinks, mount points, UNC redirections, or volume identities resolve to the intended target. Physical acceptance remains blocked until those checks and independent runtime write monitoring are implemented.

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
