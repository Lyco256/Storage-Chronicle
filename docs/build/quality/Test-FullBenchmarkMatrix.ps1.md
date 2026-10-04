# Test-FullBenchmarkMatrix.ps1

Runs each required BenchmarkDotNet lane in a separate artifact folder and validates the expected full-compressed JSON methods. `-PortableOnly` is diagnostic and always ineligible. `-IncludeMft` requires an explicit existing external `-ArtifactRoot`, a verified physical preflight, v2 create-new seed evidence, and separately collected independent process-attributed write-monitor evidence before any suite is launched. MFT artifacts may not default under the repository; repository, OneDrive/Documents, reparse, synchronized, and protected host roots are rejected by the shared workflow contracts.

For the MFT child process, the runner passes verified preflight values plus VHDX file identity, seed workload/oracle roots, creator and monitor evidence paths/SHA-256 digests, and process identities through scoped environment variables. It restores the caller's process environment after writing the matrix manifest. Both this runner and `Test-FinalAcceptance.ps1` revalidate provenance; creator-owned records alone never make a result eligible.

Public parameters: `-Configuration`, `-NoRestore`, `-IncludeMft`, `-PortableOnly`, `-ArtifactRoot`, `-MftEvidencePath`, `-MftCreationEvidencePath`, and `-MftWriteMonitorEvidencePath`. It consumes `MftPhysicalSeed.Contracts.ps1` and `MftSeedWorkflow.Contracts.ps1`. Failures before or during execution write an ineligible manifest and return nonzero; missing external evidence produces `NOT_EXECUTED` before BenchmarkDotNet starts.

Exact static and mocked workflow/provenance contracts are in `Test-MftPhysicalSeedContracts.ps1`; repository mirror validation is `build/quality/Test-DocMirror.ps1`, and the fast repository gate is `build/Test-Fast.ps1 -NoRestore`. No storage operations are performed by these contract tests. This handoff did not execute a physical MFT matrix.

Relevant requirements: `Requirements/03_TEST_AND_QUALITY_REQUIREMENTS.md` §5 and §9 and `Requirements/19_AGENT_INTEGRATION_QUALITY.md` §4 and §6. Evidence remains acceptance evidence only; it does not alter product contracts or thresholds.
