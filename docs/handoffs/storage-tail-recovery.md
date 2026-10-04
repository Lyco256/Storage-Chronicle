# Handoff: storage-tail-recovery

## Scope and requirement interpretation

Implemented the Req.12 recovery choice confirmed by the user: truncate only an incomplete uncommitted tail. Complete CRC-validated records preceding that tail remain byte-for-byte intact. This changes no requirement files. A complete CRC-invalid frame, malformed header, manifest-backed segment, compressed segment, or file outside the storage-owned boundary is not repaired or truncated.

## Changes

- Reopen only manifest-free `.open` segments for append; finalized segments are never append candidates.
- Before appending to a candidate, re-open it exclusively, revalidate its canonical storage-root boundary, filename/header identity, header, and all complete frame CRCs, then shorten only an incomplete trailing frame.
- Preserve invalid complete frames without mutation and continue new writes in a fresh segment.
- Add isolated storage-engine tests for incomplete-tail recovery and CRC-corrupt complete-frame preservation.
- Synchronize the two source mirror documents.

## Validation

- `dotnet test tests/StorageChronicle.Storage.Tests/StorageChronicle.Storage.Tests.csproj --no-restore --verbosity minimal` — passed (20/20).
- `pwsh -NoProfile -ExecutionPolicy Bypass -File build/Build.ps1` — passed, 0 warnings / 0 errors.
- `pwsh -NoProfile -ExecutionPolicy Bypass -File build/Test-Fast.ps1 -NoRestore` — passed for all 24 non-privileged Fast projects, including Storage (20/20).
- `pwsh -NoProfile -ExecutionPolicy Bypass -File build/quality/Test-DocMirror.ps1` — passed.
- `git diff --check` — passed.
- Tests use temporary owned fixture directories only; no physical media or user history was accessed.

## Limitations

- This handoff covers storage tail recovery only. It does not establish physical-machine acceptance, the exhaustive read-only audit, or release readiness.
