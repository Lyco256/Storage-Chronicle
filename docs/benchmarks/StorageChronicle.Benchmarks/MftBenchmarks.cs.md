# MftBenchmarks.cs

The MFT benchmark uses one measured iteration with no warmup and explicitly does not enforce a Windows power plan. It requires a physically verified dedicated VHDX-backed NTFS seed; missing physical preflight remains fail-closed. The measured matrix includes 10K, 100K, and 1M public enumeration plus unchanged zero-candidate and small-candidate metadata-query gates.

## Role

Measures public Windows NTFS MFT enumeration and reconciliation import against a dedicated `SC_TEST_MFT_VOLUME` backed by an attached dynamic file-backed VHDX. It accepts only a physical preflight record and emits host hardware plus VHDX/disk/volume/marker identities.

## Public types and responsibilities

`WindowsMftBenchmarks` performs the real one-million-entry benchmark only after the physical-seed preflight schema/status/eligibility and core identities match the benchmark configuration. Actual public MFT enumeration independently confirms the entry count.

## Inputs and outputs

It reads only metadata returned by the public NTFS enumeration API and verified physical-preflight values supplied through `STORAGE_CHRONICLE_MFT_*` environment variables. The matrix runner supplies the verified v2 seed file identity, workload/oracle paths, creator and independent-monitor evidence paths and SHA-256 values, process identities, and external artifact root. The benchmark rechecks those evidence hashes and run/target/process bindings before accepting the preflight. The five measured methods emit `StorageChronicle.MftBenchmarkEvidence.v1` to `STORAGE_CHRONICLE_MFT_EVIDENCE_PATH`, including dataset/enumeration/candidate/detail-query/generated-canonical/drop counters, elapsed time, allocation, physical OS/build/CPU/memory, VHDX type/size/path/file identity, disk and volume identities, marker/run/workload/oracle paths, evidence hashes, and process identities. Candidate metadata first uses the ordinary metadata path and retries `SeBackupPrivilege` only for access-denied candidates; both file and directory metadata handles receive low-I/O hints when supported. It writes BenchmarkDotNet output under the caller-selected artifact directory and never reads file contents.

## Dependencies

Depends on `StorageChronicle.Platform.Windows.Ntfs`, `System.Text.Json`, and BenchmarkDotNet; the source is excluded from non-Windows benchmark builds.

## Invariants

The benchmark does not create, resize, delete, or repair a USN journal and does not make raw `$MFT` sector parsing a source of truth. A volume with fewer than one million returned entries fails closed. Correctness evidence must be a new child of the externally approved, pre-existing ArtifactRoot; the parent must already exist and the output uses `FileMode.CreateNew` plus flush-to-disk. It never creates arbitrary parents or overwrites existing evidence.

## Threading and lifetime

BenchmarkDotNet owns the benchmark lifetime; enumeration is asynchronous and cancellation is provided by the underlying collector boundary.

## Failure behavior

Non-Windows hosts, missing/mismatched or tampered preflight/creation/monitor evidence, incomplete monitor traces, incorrect process identities, unsafe output roots, an existing output target, missing output parent, insufficient entry count, or enumeration failure produce a failed benchmark run rather than a fabricated result. The external monitor must still be separately supplied; creator-authored records alone are insufficient.

## Tests

The public API boundary is covered by `tests/StorageChronicle.Platform.Windows.Ntfs.Tests` and the physical capability lane by `tests/StorageChronicle.Platform.Windows.Integration.Tests`. Req19 offline source contracts in `build/quality/Test-MftPhysicalSeedContracts.ps1` assert the added output fields, provenance inputs, parent refusal, and CreateNew behavior without executing this Windows-only benchmark.

## OS constraints

Requires Windows 10 22H2 or Windows 11 and a disposable/dedicated NTFS volume with the required access.

## Change-sensitive contracts

The physical preflight schema, disk/volume/marker identity fields, host CPU/memory/OS fields, VHDX file identity, workload/oracle paths, creator and monitor evidence paths/hashes, process identities, external ArtifactRoot binding, 10K/100K/1M methods, one-million-entry setup threshold, public-API boundary, and metadata-only behavior are release contracts. VM CPU/memory fields are retired. Physical runs require matching preflight, creator, independent monitor, and output-root identities. This handoff did not execute a physical MFT run; acceptance remains blocked until external monitor evidence is supplied and all physical gates pass.
