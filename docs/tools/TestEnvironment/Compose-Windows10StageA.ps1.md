# Compose-Windows10StageA.ps1

Composes `StorageChronicle.Windows10StageAAcceptance.v1` only from real Windows 10 22H2 Hyper-V evidence: the completed `Invoke-WindowsTestLab.ps1` Agent/real-I/O manifest, the eligible Windows 10 privileged capability matrix, the eleven-case Hyper-V installer manifest, and independent real Cloud Files and no-driver checks. It requires `SC-Test-W10`, VM execution, x64/22H2 evidence, existing referenced files, and `AcceptanceEligible=true` on every input. Missing, diagnostic, fixture-only, malformed, or partial inputs produce `NOT_EXECUTED` and exit code 2.

The script is an evidence composer; it does not run a VM, install software, change a volume, or create synthetic Stage A results. It imports the shared `build/quality/AcceptanceContracts.ps1` capability list and rejects privileged evidence with a missing, duplicate, or extra capability name. A passing artifact is therefore possible only after the real TestLab, privileged, installer, Cloud Files, and no-driver checks have run. The output is consumed by `tools/PhysicalAcceptance/Finalize-Windows10PhysicalAcceptance.ps1` and `build/quality/Test-FinalAcceptance.ps1`.

Tests: PowerShell parser validation and the no-input fail-closed path are host-testable. A `PASSED` result requires real Windows 10 22H2 Hyper-V artifacts.
