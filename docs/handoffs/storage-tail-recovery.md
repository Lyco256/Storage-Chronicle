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
- Make all benchmark roots collision-safe: exclusively create the parent fixture directory with `CreateDirectoryW`, keep the product data in an empty child directory, bind cleanup to a GUID owner marker and direct OS temp-root containment, and refuse reparse-point cleanup. Add an architecture source contract for the cleanup boundary.
- Pin the BenchmarkDotNet single-iteration/no-warmup job without enforcing a Windows power plan; prevent SDK artifact-path redirection from breaking BenchmarkDotNet-generated project-reference builds.
- Synchronize source mirrors for SegmentLog, AppendOnlyStorageEngine, and SqliteIndex.

## Validation

- `dotnet test tests/StorageChronicle.Storage.Tests/StorageChronicle.Storage.Tests.csproj --no-restore --verbosity minimal` — passed (22/22), including tail recovery, automatic missing/corrupt-index rebuild, and unsupported-schema preservation.
- `pwsh -NoProfile -ExecutionPolicy Bypass -File build/Build.ps1` — passed, 0 warnings / 0 errors.
- `pwsh -NoProfile -ExecutionPolicy Bypass -File build/Test-Fast.ps1 -NoRestore` — passed for all 24 non-privileged Fast projects, including Storage (22/22).
- `pwsh -NoProfile -ExecutionPolicy Bypass -File build/quality/Test-DocMirror.ps1` — passed.
- `dotnet run --project benchmarks/StorageChronicle.Benchmarks/StorageChronicle.Benchmarks.csproj --no-build -- --list flat` — listed `SegmentAppendAndSqliteIndex1M`; benchmark measurements were not run.
- `build/quality/Test-FullBenchmarkMatrix.ps1` PowerShell AST parse — passed; 1M benchmark is now an expected method in the AppendAndCompression suite.
- `dotnet build benchmarks/StorageChronicle.Benchmarks/StorageChronicle.Benchmarks.csproj --no-restore --verbosity minimal` — passed, 0 warnings / 0 errors.
- `dotnet test tests/StorageChronicle.Architecture.Tests/StorageChronicle.Architecture.Tests.csproj --no-restore --verbosity minimal` — passed (14/14), including the benchmark fixture ownership/cleanup contract.
- After centralizing the power-plan-safe single-iteration job, `dotnet test tests/StorageChronicle.Architecture.Tests/StorageChronicle.Architecture.Tests.csproj -c Release --no-restore` — passed (15/15).
- `dotnet build benchmarks/StorageChronicle.Benchmarks/StorageChronicle.Benchmarks.csproj -c Release --no-restore` — passed, 0 warnings / 0 errors.
- `pwsh -NoProfile -ExecutionPolicy Bypass -File build/quality/Test-DocMirror.ps1` and PowerShell AST parsing for `Test-FullBenchmarkMatrix.ps1` / `Test-FinalAcceptance.ps1` — passed.
- Final `pwsh -NoProfile -ExecutionPolicy Bypass -File build/Test-Fast.ps1 -NoRestore` — passed all configured Fast projects, including Storage 22/22 and Architecture 15/15; `git diff --check` passed.
- `pwsh -NoProfile -ExecutionPolicy Bypass -File build/Test-Fast.ps1 -NoRestore` — passed all 24 Fast projects, including Architecture (14/14) and Storage (22/22).
- A guarded 1M Storage BenchmarkDotNet launch exposed runner issues before measurement: BDN switched Balanced to High Performance and reported restoring it, then its generated project-reference build failed. Zero benchmark iterations ran. The active plan was read-only verified as Balanced afterward; the failed-run artifacts were retained at `C:\Users\lyco2\AppData\Local\Temp\StorageChronicle-Storage1M-1fbeffd82b4b4e7e999bd3d199cb5142`.
- Follow-up work centralizes the single-iteration/no-warmup/no-power-plan job and prevents SDK artifact-path redirection in generated builds. Architecture tests passed 15/15 and the benchmark project built with zero warnings/errors. A guarded InProcessEmit experiment used exactly one 1M Storage benchmark and stayed on Balanced, but although the 1M method completed its record-count assertion (~1.9 minutes), BenchmarkDotNet rejected the run as too long and emitted no valid result. Its artifacts are retained under `C:\Users\lyco2\AppData\Local\Temp\StorageChronicle-StorageAppend1M-Diagnostic-b2326111aed4430da48dd0a3d8a6ca3d`; this is not performance evidence. InProcessEmit was removed. Next attempt uses the ordinary process toolchain through a short temporary drive mapping to test whether the generated project build can stay within Windows path limits.
- The ordinary .NET CLI process-toolchain retry succeeded using a temporary `Z:` mapping (released after the run): one benchmark, one 1M record-count assertion, exit 0, 108.339 s measured mean (N=1), 21,534,539,896 managed bytes allocated, 1,814,000 Gen0 / 1,296,000 Gen1 / 33,000 Gen2 collections. BDN global time was 385.83 s. Host: Windows 11 25H2 build 26200.9457, Intel i5-1235U, .NET SDK 10.0.401 / runtime 10.0.12. PowerPlanMode was all-zero and the host remained Balanced. Report files are retained in `C:\Users\lyco2\AppData\Local\Temp\StorageChronicle-StorageAppend1M-Process-9348cecd420a47e6a07074c4c757145c`. This is a single-iteration diagnostic for one suite, not full portable/MFT acceptance or a release statistical claim; no benchmark output is added to the repository.
- `git diff --check` — passed.
- Integrated with `--no-ff` into `devenv` without conflicts: tail recovery `dde919b`, automatic SQLite recovery `9d78c50`, 1M small-record fixture `247f0f1`, and benchmark fixture cleanup hardening `8314865`. Post-merge `pwsh -NoProfile -ExecutionPolicy Bypass -File build/Test-All.ps1` passed at `8314865` (all configured phases completed; build 0 warnings / 0 errors), including all 24 Fast projects, Quality/coverage, DocMirror, MFT/retired-VM offline contracts, and UI. Storage passed 22/22 and Architecture 14/14. Privileged Windows acceptance and the resource-intensive 1M benchmark measurement were not run.
- Benchmark runner/matrix hardening was integrated as `f7a6e2f` (`5104fd7` feature commit) with a clean no-conflict merge. Post-merge `build/Test-All.ps1` completed successfully: build 0 warnings/errors, all 24 Fast projects, Quality/coverage, 31/31 MFT contracts, 17/17 retired-VM contracts, DocMirror, and UI; Storage 22/22 and Architecture 15/15. A separate 1M Storage diagnostic passed with exactly 1,000,000 indexed records (108.339 s, one sample, 21,534,539,896 managed bytes) on a power-plan-safe out-of-process job; this is not full matrix/release acceptance. Requirement 37 stays `FAIL / DO_NOT_RUN`; `main` remains untouched.
- Tests use temporary owned fixture directories only; no physical media or user history was accessed.

## Limitations

- This handoff covers Req.12 storage recovery only. It does not establish physical-machine acceptance, the exhaustive read-only audit, or release readiness.
