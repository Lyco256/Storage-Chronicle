# MftBenchmarks.cs

The MFT benchmark uses one measured iteration with no warmup over the configured capability volume; a missing capability remains fail-closed in the matrix wrapper. The measured matrix includes 10K, 100K, and 1M public enumeration plus unchanged zero-candidate and small-candidate metadata-query gates.

## Role

Measures public Windows NTFS MFT enumeration and reconciliation import against a supplied dedicated volume labeled `SC_TEST_MFT_VOLUME` with a user-approved marker.

## Public types and responsibilities

`WindowsMftBenchmarks` performs the real one-million-entry capability benchmark and rejects hosts or volumes that cannot satisfy the required workload.

## Inputs and outputs

It reads only metadata returned by the public NTFS enumeration API and the `STORAGE_CHRONICLE_MFT_VOLUME`, `STORAGE_CHRONICLE_MFT_VOLUME_LABEL`, and `STORAGE_CHRONICLE_MFT_MARKER_PATH` environment variables. The five measured methods also emit `StorageChronicle.MftBenchmarkEvidence.v1` to `STORAGE_CHRONICLE_MFT_EVIDENCE_PATH`, including dataset/enumeration/candidate/detail-query/generated-canonical/drop counters, elapsed time, allocation, and the required OS/VM/VHDX environment fields. Candidate metadata first uses the ordinary metadata path and retries `SeBackupPrivilege` only for access-denied candidates; both file and directory metadata handles receive low-I/O hints when supported. It writes BenchmarkDotNet output under the caller-selected artifact directory and never reads file contents.

## Dependencies

Depends on `StorageChronicle.Platform.Windows.Ntfs`, `System.Text.Json`, and BenchmarkDotNet; the source is excluded from non-Windows benchmark builds.

## Invariants

The benchmark does not create, resize, delete, or repair a USN journal and does not make raw `$MFT` sector parsing a source of truth. A volume with fewer than one million returned entries fails closed.

## Threading and lifetime

BenchmarkDotNet owns the benchmark lifetime; enumeration is asynchronous and cancellation is provided by the underlying collector boundary.

## Failure behavior

Non-Windows hosts, missing device configuration, insufficient entry count, or enumeration failure produce a failed benchmark run rather than a fabricated result.

## Tests

The public API boundary is covered by `tests/StorageChronicle.Platform.Windows.Ntfs.Tests` and the physical capability lane by `tests/StorageChronicle.Platform.Windows.Integration.Tests`.

## OS constraints

Requires Windows 10 22H2 or Windows 11 and a disposable/dedicated NTFS volume with the required access.

## Change-sensitive contracts

The environment variables, dedicated-volume marker/label, required evidence environment fields, 10K/100K/1M methods, one-million-entry setup threshold, public-API boundary, and metadata-only behavior are release contracts. The full matrix must provide `STORAGE_CHRONICLE_MFT_VM_CPU_COUNT`, `STORAGE_CHRONICLE_MFT_VM_MEMORY_MIB`, `STORAGE_CHRONICLE_MFT_VHDX_TYPE`, and `STORAGE_CHRONICLE_MFT_VHDX_SIZE_GIB`; missing values keep the artifact ineligible.
