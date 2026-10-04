# Handoff: storage-tail-recovery

## Scope and requirement interpretation

Implemented the Req.12 recovery choice confirmed by the user: truncate only an incomplete uncommitted tail. Complete CRC-validated records preceding that tail remain byte-for-byte intact. This changes no requirement files. A complete CRC-invalid frame, malformed header, manifest-backed segment, compressed segment, or file outside the storage-owned boundary is not repaired or truncated.

## Changes

- Reopen only manifest-free `.open` segments for append; finalized segments are never append candidates.
- Before appending to a candidate, re-open it exclusively, revalidate its canonical storage-root boundary, filename/header identity, header, and all complete frame CRCs, then shorten only an incomplete trailing frame.
- Preserve invalid complete frames without mutation and continue new writes in a fresh segment.
- On startup, rebuild the SQLite index/state cache automatically when the DB is missing beside authoritative segments or when SQLite integrity checking reports structural corruption. Do not classify unsupported schema, busy, permission, or unrelated I/O failures as corruption.
- Add isolated storage-engine tests for incomplete-tail recovery, CRC-corrupt complete-frame preservation, automatic missing/corrupt SQLite rebuild, and preservation of unsupported-schema DBs.
- Add the missing 1M small-record Storage benchmark fixture, generating and appending deterministic metadata-only Canonical Events in bounded 512-record batches and asserting exactly 1,000,000 indexed records. Include it in the full benchmark matrix without executing the resource-intensive measurement during ordinary validation.
- Synchronize source mirrors for SegmentLog, AppendOnlyStorageEngine, and SqliteIndex.

## Validation

- `dotnet test tests/StorageChronicle.Storage.Tests/StorageChronicle.Storage.Tests.csproj --no-restore --verbosity minimal` — passed (22/22), including tail recovery, automatic missing/corrupt-index rebuild, and unsupported-schema preservation.
- `pwsh -NoProfile -ExecutionPolicy Bypass -File build/Build.ps1` — passed, 0 warnings / 0 errors.
- `pwsh -NoProfile -ExecutionPolicy Bypass -File build/Test-Fast.ps1 -NoRestore` — passed for all 24 non-privileged Fast projects, including Storage (22/22).
- `pwsh -NoProfile -ExecutionPolicy Bypass -File build/quality/Test-DocMirror.ps1` — passed.
- `dotnet run --project benchmarks/StorageChronicle.Benchmarks/StorageChronicle.Benchmarks.csproj --no-build -- --list flat` — listed `SegmentAppendAndSqliteIndex1M`; benchmark measurements were not run.
- `build/quality/Test-FullBenchmarkMatrix.ps1` PowerShell AST parse — passed; 1M benchmark is now an expected method in the AppendAndCompression suite.
- `git diff --check` — passed.
- Integrated with `--no-ff` into `devenv` without conflicts: tail recovery at `dde919b`, then automatic SQLite recovery at `9d78c50`. Post-merge `pwsh -NoProfile -ExecutionPolicy Bypass -File build/Test-All.ps1` passed at `9d78c50` (exit 0), including all 24 Fast projects, Quality/coverage, DocMirror, MFT/retired-VM offline contracts, and UI. Storage passed 22/22; build warnings/errors: 0. Privileged Windows acceptance was not run.
- Tests use temporary owned fixture directories only; no physical media or user history was accessed.

## Limitations

- This handoff covers Req.12 storage recovery only. It does not establish physical-machine acceptance, the exhaustive read-only audit, or release readiness.
