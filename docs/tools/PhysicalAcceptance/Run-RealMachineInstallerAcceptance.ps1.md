# Run-RealMachineInstallerAcceptance.ps1

## Role

Human-started entry point for the Windows 10 22H2 or Windows 11 physical installer matrix. This is a deliberately destructive installer test and is not suitable for an ordinary workstation. Static checks and a read-only preflight run before the installer harness can request the exact dedicated-PC/run confirmation.

## Inputs and writes

Requires a marked TestLab Workload root, a new explicit local fixed-NTFS `EvidenceRoot`, an independently reviewed SHA-256 fingerprint for `hash-manifest.json`, and non-secret credential references/user names. The root and volume markers must agree on schema, Workload role, TestId, label, NTFS, and volume identity. Every manifest path must be a unique safe relative path contained beneath the bundle and every payload hash must match.

Only a new GUID-specific evidence directory is created. Preflight/failure evidence uses create-new semantics and does not overwrite prior files. It rejects synchronized folders, repository/bundle/application overlap, reparse paths, and evidence/TestDataRoot overlap. The installer itself can install, update, roll back, uninstall, create its own ProgramData history, and change/restore ACLs on a newly created fixture; those operations require a dedicated physical PC and exact user confirmation in `Test-Installer.ps1`. The app's run-created history is retained.

## Invariants and failure behavior

The bundle fingerprint and payload hashes are verified before machine changes. Existing product registration, service, install directory, or ProgramData product root causes refusal. Virtualized models, wrong OS/build, non-admin execution, unmarked/wrong-volume fixture roots, and existing evidence paths fail closed. A preflight/failure artifact is non-acceptance evidence and never makes the run eligible. No cleanup/deletion is performed by this entry point.

## Dependencies and tests

Depends on Windows PowerShell, Storage cmdlets for read-only volume identity checks, the generated acceptance bundle, and `Test-Installer.ps1`. Parser validation and static installer manifest tests cover the gates; actual Windows installer, ACL, and service side effects remain unexecuted and require independent runtime write monitoring before use.
