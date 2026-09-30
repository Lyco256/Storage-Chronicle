# External-media file access rights handoff

## Scope

Branch: `feat/external-media-rights`  
Base: `devenv` at `4d7b95bb980da4019629ea420d8806e40a90df27`

This change narrows the handle access requested when creating a new external-media file. `CreateNewFile` now requests `FILE_WRITE_DATA`, `FILE_READ_ATTRIBUTES`, `DELETE`, and `SYNCHRONIZE` instead of `GENERIC_WRITE`; the returned `FileStream` is write-only. `MoveCreatedFile` authenticates ownership through the session's issued-stream set rather than requiring `CanRead`, so write-only create streams and read-only recovery streams can both use the existing no-replace rename flow. Closed or invalid issued handles fail closed.

The filesystem contract mirror and fixture test now describe/assert the write-only stream. No shared contracts or Requirements files were changed.

## Validation

- `dotnet build StorageChronicle.slnx --nologo`: passed, 0 warnings, 0 errors.
- `build/Test-Fast.ps1 -NoRestore`: passed all listed projects. Agent: 51 passed, 3 expected elevated/physical skips. Windows filesystem: 22/22; ExternalMedia: 34/34. No build warnings/errors.
- `build/Test-All.ps1`: passed (exit 0), including fast tests, quality/coverage, architecture/integration, UI, and the six-case mocked VirtualBox contract check. Coverage gates: Domain 82.41%, State 94.13%, Projection 89.28%, Storage 87.69%; Diff/EventStack/Settings ViewModels 92.23%/78.51%/92.51%. The separate privileged Windows acceptance lane was not run.
- `build/quality/Test-DocMirror.ps1`: passed.
- Focused Windows filesystem MTP runner: 22/22 passed, including write-only stream, rename/no-replace, and reparse fixture coverage.
- Focused ExternalMedia MTP runner: 34/34 passed.
- `git diff --check`: passed before handoff.

The first `Test-Fast.ps1` attempt from the freshly created worktree reached Architecture tests before `State.dll` had been built and failed assembly discovery. A full solution build populated required analysis inputs; the clean rerun of the fast lane then passed. A direct `dotnet test` attempt was rejected because this repository requires Microsoft.Testing.Platform; the project was run using its generated MTP executable instead.

## Safety boundaries and remaining limitations

All tests used newly created local temporary fixtures and did not access existing external media, product history, services, MSI, VHDX, or privileged acceptance paths. This least-privilege change does not authorize any real-media run.

The independent review of `devenv` identified unresolved external-media risks: the public JSON ownership marker is forgeable, PC-local recovery/import file operations have path-check TOCTOU windows, and production ancestor/reparse race resistance is incomplete. The complete current-commit source-to-sink audit, read-only host preflight, and independent process-attributed runtime write monitor remain prerequisites; physical readiness stays **NOT READY / DO NOT RUN**. No external-media ownership or race finding is claimed closed by this branch.
