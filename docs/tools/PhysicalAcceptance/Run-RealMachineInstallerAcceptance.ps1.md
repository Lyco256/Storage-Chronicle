# Run-RealMachineInstallerAcceptance.ps1

## Role

Human-started, non-elevated entry point for the Windows 10 22H2 or Windows 11 physical installer matrix. This is a deliberately mutating installer test and is not suitable for an ordinary workstation. Static checks and a read-only preflight run before the installer harness requests dedicated-PC/run confirmation. The broker refuses an already elevated process or a host with UAC disabled; each individual case is launched through `runas` only after the non-elevated parent presents its concrete mutation plan.

## Inputs and writes

Requires a marked TestLab Workload root, a new explicit local fixed-NTFS `EvidenceRoot`, an independently reviewed SHA-256 fingerprint for `hash-manifest.json`, and non-secret credential references/user names. The root and volume markers must agree on schema, Workload role, TestId, label, NTFS, and volume identity. Every manifest path must be a unique safe relative path contained beneath the bundle and every payload hash must match.

Only a new GUID-specific evidence directory is created. Preflight/failure evidence uses create-new semantics and does not overwrite prior files. It rejects synchronized folders, repository/bundle/application overlap, reparse paths, and evidence/TestDataRoot overlap. The installer itself can install, update, roll back, uninstall, create its own ProgramData history, configure service recovery as restart after 5/15/60 seconds with a one-day reset, set the HKLM Session Agent Run value, and change/restore ACLs on a newly created fixture. Those actions require a dedicated physical PC, exact per-case confirmation, and the subsequent UAC approval. The app's run-created history is retained; the receipt does not sandbox global Windows Installer effects.

## Invariants and failure behavior

The bundle fingerprint and payload hashes are verified before any case elevation. Existing product registration, service, install directory, or ProgramData product root causes refusal. Virtualized models, wrong OS/build, already elevated execution, disabled UAC, unmarked/wrong-volume fixture roots, and existing evidence paths fail closed. A preflight/failure artifact is non-acceptance evidence and never makes the run eligible. No cleanup/deletion is performed by this entry point.

## Dependencies and tests

Depends on Windows PowerShell, Storage cmdlets for read-only volume identity checks, the generated acceptance bundle, and `Test-Installer.ps1`. Parser and isolated broker protocol tests cover only source contracts and process binding; actual UAC cancellation/approval, Windows Installer, ACL, and service side effects remain unexecuted and require independent runtime write monitoring before any use. The MSI hash-to-use replacement race remains a blocking audit finding.
