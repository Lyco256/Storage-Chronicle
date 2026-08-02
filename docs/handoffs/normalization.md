# Normalization handoff

- Requirement: `Requirements/07_AGENT_NORMALIZATION.md`
- Branch: `feat/normalization`
- Starting commit: `ab607f8` (`chore(foundation): establish Storage Chronicle architecture contracts`)
- Owned paths: `src/StorageChronicle.Normalization/**`, `tests/StorageChronicle.Normalization.Tests/**`, `docs/src/StorageChronicle.Normalization/**`, and this handoff. The solution and test-project references were added to `StorageChronicle.slnx` as the explicitly requested integration references.
- Changes: added the platform-neutral `EventNormalizer`, bounded source replay and Clipboard correlation state, deterministic operation mapping, read-only ETW suppression, Rename/Move/Recycle/Restore handling, Share/Cloud/Reconciliation mapping, quality preservation/downgrade rules, sensitive-property allow-listing, cancellation, and contradictory-duplicate detection.
- Clipboard rule: a Copy association requires a matching generation, valid interval, mount/process compatibility, and an Explorer create/copy/paste observation. Cut becomes Move only after same-volume, same-File-ID, and changed-parent confirmation; cross-volume or incomplete evidence remains Create and is marked rejected.
- Tests: `dotnet test tests/StorageChronicle.Normalization.Tests/StorageChronicle.Normalization.Tests.csproj --no-restore` — passed 20/20.
- Build: `dotnet build src/StorageChronicle.Normalization/StorageChronicle.Normalization.csproj --no-restore` — passed with 0 warnings and 0 errors.
- Full-solution validation: must be rerun by the top agent after all Wave 1 projects are integrated. The shared workspace contained other feature worktree files while this branch was implemented; those changes were preserved and not edited.
- Known limitation: `IEventNormalizer` is synchronous, so cancellation is exposed by the concrete `NormalizeAsync` helper; the shared contract was not changed. Clipboard and ETW source buffers remain platform-agent responsibilities.
- Shared-contract changes: none. The Domain/Contracts model was consumed as provided.
