# External Media handoff

## Branch and ownership

- Branch: `feat/external-media`
- Requirement source: `Requirements/18_AGENT_EXTERNAL_MEDIA.md`
- Owned changes: `src/StorageChronicle.ExternalMedia/**`, `tests/StorageChronicle.ExternalMedia.Tests/**`, `docs/src/StorageChronicle.ExternalMedia/**`, and this handoff.
- Shared Domain/Contracts, Requirements, and solution files were not changed.

## Implemented

- Writer-PC directories under `.StorageChronicle/writers/<writer>` with immutable length-delimited segments, per-record CRC32C, finalized-segment SHA-256, bounded reads, and temporary-to-atomic publication.
- Writer-specific A/B manifests with separate format, event-schema, and projection versions; self-SHA and parent-SHA validation; safe segment paths; newest-valid selection; branch detection for distinct valid children; and no common-segment duplication during import.
- PC-to-PC confirmed-log import across all writer directories, persisted manifest/segment/branch ledger, duplicate prevention, concurrent-writer warning, parent-unavailable/corruption warnings, cancellation propagation, and media-only filtering.
- Mount-session predecessor links without clock correction; monitoring exclusion registration; mirror policy that rejects system/boot/recovery/EFI volumes; deleted-log recovery marker and new branch; and one-temporary-segment finalize/discard recovery.
- Honest NTFS/USN, FAT/FAT32, exFAT, read-only, unsupported, and reconciliation quality. No code creates or modifies a USN journal.

## Validation

Commands run from the repository root:

```text
dotnet test tests\StorageChronicle.ExternalMedia.Tests\StorageChronicle.ExternalMedia.Tests.csproj --no-restore --verbosity minimal
dotnet build src\StorageChronicle.ExternalMedia\StorageChronicle.ExternalMedia.csproj --no-restore --verbosity minimal
```

Validation after the ownership-safety follow-up (run from a directory outside the worktree to use installed SDK 10.0.401):

```text
dotnet build <worktree>\StorageChronicle.slnx --no-restore --verbosity minimal
& <worktree>\tests\StorageChronicle.ExternalMedia.Tests\bin\Debug\net10.0\StorageChronicle.ExternalMedia.Tests.exe
& <worktree>\tools\StorageChronicle.DocMirrorValidator\bin\Debug\net10.0\StorageChronicle.DocMirrorValidator.exe <worktree>
```

Results: full-solution build succeeded with 0 warnings and 0 errors; ExternalMedia tests passed (20 passed, 0 failed, 0 skipped); DocMirror passed. The safe, non-privileged test executables passed 162 tests across 19 suites. The one environment-gated privileged NTFS test was not executed (its opt-in variable was unset). `dotnet test` and `build/Test-Fast.ps1` cannot currently resolve the repository-pinned SDK 10.0.302 because only 9.0.203 and 10.0.401 are installed; tests were run through the already-built Microsoft.Testing.Platform executables instead.

The tests cover PC-A to PC-B handoff, same-event/segment deduplication, branch and clone interpretation, manifest corruption and version rejection, segment corruption and interruption recovery, deleted logs, read-only/FAT/exFAT/USN quality, media-only filtering, monitoring exclusion, mount sessions, ledger persistence, cancellation-sensitive APIs, and path traversal.

## Known limitations

Simultaneous independent writers are outside the supported write protocol: they are detected and surfaced as `ConcurrentWritersDetected`; valid histories remain importable and are treated as branches when they share a parent. Recovery does not fabricate missing events: non-USN or interrupted continuity remains reconciliation/unverified quality. Existing `.StorageChronicle` roots without the new ownership marker are intentionally read-only and require an explicit, separately designed user-approved migration before mirroring can resume there. Mirror reads and writes require the configured root to resolve beneath an authoritative mount point of the currently enumerated media volume; missing or mismatched mount identity fails closed. Full solution validation and final integration into `devenv`/`main` remain the top agent's responsibility.

## Bounded Requirement 18 hardening follow-up

### Changes

- Enforced the existing `MediaEventFilter` at `ExternalMediaStore.AppendSegmentAsync` before creating a segment file. The first event establishes the batch's volume, mount-session, and optional logical-media identity; every event must match. A mixed batch containing a foreign/system-volume event fails with `InvalidDataException` and leaves no `.tmp`/`.seg` output. A valid media-only batch still appends and reads back.
- Made import-ledger parsing strict and fail-closed. Required manifest/segment/branch arrays must exist with the expected types; unknown/duplicate fields, unsupported schema versions, invalid JSON, malformed hashes, and invalid branch identities are rejected. Legacy ledgers without `schemaVersion` remain readable if all three recognized fields are valid.
- Before saves, validate every `.json` ledger leaf in the dedicated directory and validate any existing destination before replacement. Corrupt/unrecognized content is not converted to an empty ledger and is never overwritten.
- Saves now use unique temporary names. Loading can recover exactly one valid interrupted temporary write only when the destination is absent. Ambiguous, malformed, or colliding recovery state is preserved and rejected.
- Added contract tests for valid and mixed-media appends, missing arrays, unsupported versions, unknown fields and sibling leaves, malformed JSON, valid temporary recovery, and preservation/no-overwrite behavior.
- Updated both source mirrors. No requirements, shared contracts, Agent callers, or files outside the assigned ownership paths were changed.

### Validation

The checkout pins .NET SDK `10.0.302`, which is not installed on this machine; available SDKs were `9.0.203` and `10.0.401`. Validation therefore used a temporary directory outside the repository containing a `global.json` selecting SDK `10.0.401` and the `Microsoft.Testing.Platform` runner, leaving the repository's SDK configuration unchanged.

```text
dotnet test <worktree>\tests\StorageChronicle.ExternalMedia.Tests\StorageChronicle.ExternalMedia.Tests.csproj --verbosity minimal
```

Result: 25 passed, 0 failed, 0 skipped.

```text
dotnet build <worktree>\tests\StorageChronicle.ExternalMedia.Tests\StorageChronicle.ExternalMedia.Tests.csproj --no-restore --verbosity minimal
```

Result: build succeeded, 0 warnings, 0 errors.

```text
<worktree>\build\Test-Fast.ps1
```

Invoked from the temporary SDK/MTP validation directory. Result: all 184 fast-suite tests passed, 0 failed, 0 skipped.

```text
dotnet build <worktree>\tools\StorageChronicle.DocMirrorValidator\StorageChronicle.DocMirrorValidator.csproj --verbosity minimal
<worktree>\tools\StorageChronicle.DocMirrorValidator\bin\Debug\net10.0\StorageChronicle.DocMirrorValidator.exe <worktree>
```

Result: validator build succeeded with 0 warnings/errors; `Doc mirror validation passed`. The final fast-suite rerun after the last code/test edits passed 185 tests (0 failed, 0 skipped); this is the authoritative final fast-suite total.

The repository-local `dotnet test` invocation under SDK 10.0.401 without MTP runner selection and `build/Test-Fast.ps1` from that default context fail because MTP 2.3 no longer supports the VSTest target with .NET 10; this is an environment/runner-selection limitation, not a test failure. With MTP selected in the temporary context, both the focused project and required fast validation pass.

No product process, installer, physical drive, privileged runner, or hardware workload was run. Test fixtures were temporary, run-owned paths used by the external-media unit tests.

### Commit

Implementation and validation changes are committed on `feat/external-media` as `b3779a5a63298b524b4816a8784e02f6b28c02b6`. This handoff update is committed immediately afterward; the final branch HEAD is reported with the handoff.

## Current top-agent sync-merge hardening

This section supersedes the older bounded-follow-up recovery and ledger claims above for the current `devenv` sync merge.

### Integrated source behavior

- Preserved `IVolumeBoundMediaFileSystem` and pinned-volume identity for removable-media I/O; no path fallback was introduced.
- Ported the media-only append-batch guard: all records must match the first event's volume, mount session, and optional logical-media identity before a temporary segment is created.
- The public `ExternalMediaStore` constructor selects the fixed PC-local intent store under `CommonApplicationData/Storage Chronicle/history/media-recovery-intents`; the interface and injected constructor are internal test seams only. CommonApplicationData is checked for normalized absolute local fixed-drive identity, UNC/network paths are refused, and existing path components are checked before creation and rechecked afterward.
- The exact framed segment length and SHA-256 are calculated incrementally while writing. The durable PC-local intent binds volume ID, writer ID, GUID temp name, length, and SHA-256 and is saved before the pinned-handle move. Recovery validates that intent and record CRC/boundaries, refuses an existing final name, and preserves unproven or corrupt temps. No post-commit media read occurs to create the append result. Intent cleanup after a successful move is best-effort; a stale intent cannot authorize a rename after the temp is gone.
- The intent store uses create-new and non-replacing publication. Save failure/cancellation removes only a temp created by that Save call; unknown names and existing intent files are preserved. Internal tests cover cancellation cleanup, restart readback, malformed/missing intents, failed intent save/removal, and a junction escape preflight where supported.
- `MediaImportLedgerStore` now rejects caller paths outside the fixed PC product directory and requires the encoded logical-media ID leaf. Marker files do not authorize paths. Only the expected base-ledger name and its GUID generation names are read; all other entries, including marker-shaped or valid-looking unrelated JSON, are preserved and block access. Ledger writes append unique immutable generations, never replacing a base ledger or prior generation. Reparse paths are checked before and after directory creation. Corrupt recognized ledgers and interrupted ledger temps are retained and fail closed; the parser validates schema fields, duplicate/unknown keys, hash shapes, and branch identities.

### Top-owned integration request

The Agent integration removes the public caller-selected `ledgerRoot`. Production imports always construct the ledger beneath the canonical CommonApplicationData product-history directory. An internal factory is available only to the Agent test assembly and redirects the collector integration test to a run-owned fixture; the test asserts an append-only generation is persisted. `MediaMirrorCoordinator` needs no explicit intent-store argument because the public `ExternalMediaStore` constructor supplies the fixed implementation.

### Validation for this sync merge

Focused `StorageChronicle.ExternalMedia.Tests`: 34 passed, 0 failed, 0 skipped. The full `build/Test-Fast.ps1` run completed with exit code 0; every listed project passed, including Agent (47 passed, 3 privileged acceptance tests skipped) and ExternalMedia (34 passed). `DocMirrorValidator` passed and `git diff --check` passed. No product process, physical media, VHDX, service, MSI, privileged runner, or real-media write was run. The five sync-merge conflicts were resolved by the top-agent integration commit `2eec568` after source, tests, and mirrored docs were reviewed.
