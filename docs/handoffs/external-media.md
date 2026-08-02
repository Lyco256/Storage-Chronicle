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

Results: ExternalMedia tests passed (13 passed, 0 failed, 0 skipped); focused build passed with 0 warnings and 0 errors.

The tests cover PC-A to PC-B handoff, same-event/segment deduplication, branch and clone interpretation, manifest corruption and version rejection, segment corruption and interruption recovery, deleted logs, read-only/FAT/exFAT/USN quality, media-only filtering, monitoring exclusion, mount sessions, ledger persistence, cancellation-sensitive APIs, and path traversal.

## Known limitations

Simultaneous independent writers are outside the supported write protocol: they are detected and surfaced as `ConcurrentWritersDetected`; valid histories remain importable and are treated as branches when they share a parent. Recovery does not fabricate missing events: non-USN or interrupted continuity remains reconciliation/unverified quality. Full solution validation and final integration into `devenv`/`main` remain the top agent's responsibility.
