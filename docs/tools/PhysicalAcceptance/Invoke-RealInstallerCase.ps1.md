# Invoke-RealInstallerCase.ps1

## Role

Runs one real physical Windows installer case on behalf of `build/package/Test-Installer.ps1`. It performs the requested MSI/service/session/ACL operations and reports assertions with evidence; it is not a read-only utility.

## Ownership and invariants

Requires elevation, exact current computer and GUID RunId, the harness-created owner receipt and one-case nonce, an external new evidence directory, and both matching TestLab Workload/NTFS volume markers. The fixture root must be outside system/application/repository/bundle/synchronized folders; install/history paths must equal the canonical Program Files and ProgramData product locations. The receipt binds paths, MSI hashes, host, run, TestDataRoot, and the initial user confirmation. Immediately before any case-side mutations, the driver prints the exact case, computer, run, and affected state, validates the case-bound phrase, and reads the same phrase from standard input; a missing or incorrect phrase fails closed before the case switch. The parent presents the case paths and base/update/rollback MSI hashes, asks the user to type the phrase for that one case, then supplies the phrase over the child’s standard input. Direct invocation also requires an explicit interactive line in addition to any receipt/nonce. The driver verifies run-owned product/service state before repair/update/rollback/uninstall and checks prior case results before reinstall/retention cases.

It may mutate only the Storage Chronicle installer state explicitly named by the receipt and newly created GUID fixtures/evidence. It never deletes history. ACL fixture cleanup is restricted to the exact newly created empty child after ACL restoration; failure preserves that child/evidence. Evidence/results use `CreateNew`; no existing output is replaced. MSI effects are not reversible in the general case, so this script must never be run on a normal user PC.

## Dependencies, failures, and tests

Depends on Windows Installer, SCM, ACL, interactive-session tools, and its parent harness. Missing/mismatched receipts, markers, host, volume, nonce, hashes, expected preceding case, or owned product/service fail closed. Static assertions live in `tests/StorageChronicle.Installer.Tests/InstallerManifestTests.cs`; parser/static tests do not establish runtime safety. Physical execution remains blocked pending complete static and independent runtime write audits and explicit user preparation/approval.
