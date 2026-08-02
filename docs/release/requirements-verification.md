# Requirements verification

This matrix records executable evidence for the requirement families. Requirement markdown remains the authority; this file records only verification paths.

| ID | Requirement family | Evidence | Status |
|---|---|---|---|
| V-01 | architecture and stable contracts | `tests/StorageChronicle.Architecture.Tests`, `StorageChronicle.slnx`, `docs/decisions` | verified |
| V-02 | source/normalization/state/storage | normalization, state, storage, and integration test projects | verified |
| V-03 | Windows filesystem/NTFS/session | platform test projects and `build/Test-WindowsPrivileged.ps1` | boundary verified; privileged hardware pending |
| V-04 | external media and mount history | `StorageChronicle.ExternalMedia.Tests` and filesystem tests | verified |
| V-05 | Event Stack and Diff View | projection, Event Stack, Diff View, and headless tests | verified |
| V-06 | IPC and bounded Agent pipeline | Agent tests and end-to-end smoke test | verified |
| V-07 | recovery, corruption, cancellation, capacity | Storage, State, Agent, and Normalization failure-path tests | verified |
| V-08 | documentation synchronization | `build/quality/Test-DocMirror.ps1` and DocMirrorValidator | verified |
| V-09 | packaging | WiX 6.0.2 project, manifest tests, `build/package/Build-Installer.ps1` | project builds; physical install matrix is release-environment verification |
| V-10 | performance acceptance | BenchmarkDotNet project and baseline report | harness ready; hardware thresholds require measured release run |

No row grants permission to weaken source quality, retain contents/hashes, delete history, or synthesize descendant events.
