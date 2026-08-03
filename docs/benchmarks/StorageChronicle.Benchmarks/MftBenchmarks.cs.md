# MftBenchmarks.cs

The MFT benchmark uses one measured iteration with no warmup over the configured capability volume; a missing capability remains fail-closed in the matrix wrapper.

## Role

Measures public Windows NTFS MFT enumeration and reconciliation import against a supplied dedicated volume.

## Public types and responsibilities

`WindowsMftBenchmarks` performs the real one-million-entry capability benchmark and rejects hosts or volumes that cannot satisfy the required workload.

## Inputs and outputs

It reads only metadata returned by the public NTFS enumeration API and the `STORAGE_CHRONICLE_MFT_VOLUME` device-path environment variable. It writes BenchmarkDotNet output under the caller-selected artifact directory and never reads file contents.

## Dependencies

Depends on `StorageChronicle.Platform.Windows.Ntfs` and BenchmarkDotNet; the source is excluded from non-Windows benchmark builds.

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

The environment variable, one-million-entry threshold, public-API boundary, and metadata-only behavior are release contracts.
