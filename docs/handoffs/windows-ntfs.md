# Windows NTFS handoff

- Requirement: `Requirements/13_AGENT_WINDOWS_NTFS_COLLECTOR.md`
- Branch: `feat/windows-ntfs`, based on integrated `devenv` commit `d001364`.
- Owned paths changed: `src/StorageChronicle.Platform.Windows.Ntfs/**`, `tests/StorageChronicle.Platform.Windows.Ntfs.Tests/**`, `docs/src/StorageChronicle.Platform.Windows.Ntfs/**`, and this handoff. Shared contracts, solution references, and the Windows FileSystem collector were not changed.
- Implementation: added the limited `CreateFileW`/`DeviceIoControl` boundary for `FSCTL_QUERY_USN_JOURNAL`, `FSCTL_READ_USN_JOURNAL`, and `FSCTL_ENUM_USN_DATA`; documented wire layouts; Journal ID/First/Next/Lowest USN handling; no-journal/no-create behavior; Journal ID and truncation gaps; bounded waiting reads; access-denied/media-removed status; cancellation; public API MFT enumeration; File Reference sequence protection; rename pairing; and reconciliation candidates.
- Safety invariants: no raw `$MFT` sector parsing, journal create/delete/resize calls, file-content reads, content hashes, descendant synthetic events, or whole-volume scan in the normal read path.
- Tests: `dotnet test tests/StorageChronicle.Platform.Windows.Ntfs.Tests/StorageChronicle.Platform.Windows.Ntfs.Tests.csproj --no-restore` — passed 17/17 in both default and `STORAGE_CHRONICLE_RUN_PRIVILEGED_NTFS=1` opt-in runs.
- Build: `dotnet build src/StorageChronicle.Platform.Windows.Ntfs/StorageChronicle.Platform.Windows.Ntfs.csproj --no-restore` — passed with 0 warnings and 0 errors.
- Integrated validation: `dotnet build StorageChronicle.slnx --no-restore` and `dotnet test StorageChronicle.slnx --no-restore` — passed with 0 warnings/errors and all discovered tests successful; `dotnet run --project tools/StorageChronicle.DocMirrorValidator/StorageChronicle.DocMirrorValidator.csproj --no-restore` also passed.
- Privileged validation: `WindowsPrivilegedTests.QueriesExistingJournalWithoutCreatingOrResizing` is opt-in with `STORAGE_CHRONICLE_RUN_PRIVILEGED_NTFS=1` and optional `STORAGE_CHRONICLE_NTFS_DEVICE`; it only queries the existing journal and tolerates expected access-denied/media-locality conditions. Synthetic tests remain the default.
- Shared-contract request: none. The existing `ISourceEventCollector` contract is sufficient; `IUsnJournalReader`, `INtfsApi`, and `IMftEnumerator` remain local replaceable boundaries.
- Known integration follow-up: the top agent must retain existing solution references and rerun Windows privileged, integration, and full-solution tests after this branch is merged.

## Top-agent ETW follow-up (devenv, 2026-09-30)

- Added an internal ETW session/factory boundary so unit tests inject a fake session and never start a host kernel ETW session.
- Added deterministic coverage for process/file-event translation, correlated and unknown attribution, read-observation tagging, transient process-exit delivery, bounded-queue overflow and gap reporting, cancellation/disposal, unsupported-platform behavior, and startup/processing failures.
- `dotnet test tests/StorageChronicle.Platform.Windows.Ntfs.Tests/StorageChronicle.Platform.Windows.Ntfs.Tests.csproj --no-restore -v:minimal`: 23/23 passed, 0 warnings/errors.
- `./build/Test-Fast.ps1`: exit 0; Agent 47 passed with 3 expected physical-environment skips; all other included suites passed. The physical ETW capability was not run.
- `dotnet run --project tools/StorageChronicle.DocMirrorValidator --no-restore -- .`: passed.
- The current-commit physical source-to-sink audit is still incomplete and must be refreshed after this production ETW change. This test seam does not certify live ETW behavior or authorize a physical run.
