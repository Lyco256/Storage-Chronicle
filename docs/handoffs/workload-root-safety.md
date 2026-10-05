# Workload protected-root safety handoff

## Scope and changes

- Updated `tools/StorageChronicle.FileMutationWorkload/Program.cs` to reject workload roots inside the current user profile, Documents, ProgramData, Windows, Program Files, OneDrive environment roots, and the repository before inspecting its markers or mutating files. Containment uses normalized path-relative boundaries, not substring matching.
- Added architecture-level policy tests for each available Windows special-folder root, all three OneDrive environment variables, the repository, and a similarly prefixed sibling path.
- Added the architecture-test project reference and `InternalsVisibleTo` access needed to test the safety predicate without running the mutating workload.
- Synchronized the documentation mirrors for every changed source/project file.

## Validation

- `dotnet build StorageChronicle.slnx --verbosity minimal` — passed before the final test-only coverage expansion; 52 projects, 0 warnings, 0 errors.
- `./build/Test-Fast.ps1` — passed after all changes; every fast-suite project passed, including Architecture 18/18, 0 build warnings/errors.
- `./build/quality/Test-DocMirror.ps1` — passed.
- `git diff --check` — passed.

## Safety boundary and limitations

- No file-mutation workload, VHDX setup, installer, service, administrator, or other privileged/physical acceptance action was run.
- This change closes the lexical protected-root gap only. Caller-supplied marker authenticity, adversarial races after path preflight, process-attributed runtime write monitoring, and acceptance on a dedicated disposable target remain separate audit/acceptance items. Overall release/physical-read-only gate remains blocked; this handoff is not permission to run the workload on existing user data.
