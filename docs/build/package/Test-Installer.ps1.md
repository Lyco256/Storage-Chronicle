# Test-Installer.ps1

## Role and cases

Runs the eleven Requirement 20 installer cases through the physical Windows driver. Physical host inventory and evidence preparation stay in an ordinary, non-elevated Windows PowerShell 5.1 process. VM and non-physical execution are rejected; the script never elevates itself or launches the whole harness as administrator.

## Authorization sequence

Before any case, physical execution requires enabled UAC, ordinary parent integrity, the exact declared PC/OS, the externally reviewed bundle-manifest fingerprint, exact manifest membership and hashes for the base/update/rollback MSI files and driver, a marked fixed local NTFS fixture, canonical product paths, no detected pre-existing product/service/install/ProgramData state, and a new local evidence destination. The run-level receipt and evidence directory are created with create-new semantics only after this read-only inventory and dedicated-PC confirmation.

The broker requires a non-elevated filtered token whose account belongs to local Administrators, `EnableLUA=1`, and `ConsentPromptBehaviorAdmin` equal to 1, 2, 3, or 4. Values 0 and 5 are rejected: 0 suppresses the consent/credential prompt, and 5 may not prompt for the PowerShell executable. Missing/unknown values fail closed. This gate is checked in the launcher and again in this harness; automated tests inspect source but do not read the current host's UAC policy.

For each case, the parent prints PC, RunId, case/action, all MSI filenames and SHA-256 values, install/history/evidence/log paths, and case-specific known Windows Installer, Program Files, ProgramData, SCM/service, ACL, and registry effects. The service case explicitly discloses 5s/15s/60s recovery restarts and the MSI-authored HKLM Run value `StorageChronicleSessionAgent=[INSTALLFOLDER]StorageChronicle.SessionAgent.exe`. The parent requires the exact `I AUTHORIZE STORAGE CHRONICLE CASE <case> ON <computer> RUN <guid>` phrase exactly once before requesting one `runas` launch. The elevated child does not ask for interactive input; it validates the phrase carried only by the authenticated Grant against its generated PC/run/case phrase. The phrase is not passed on the command line or standard input. After UAC approval, the elevated PowerShell process is hidden; the Windows consent prompt remains the OS-owned visible dialog.

Immediately before that one launch, the parent creates a cryptographically random named-pipe name and an explicit protected `PipeSecurity` DACL containing only the current user's SID. The pipe is created using `NamedPipeServerStreamAcl.Create` with `additionalAccessRights=0`, which is compatible with Windows PowerShell 5.1/.NET Framework; it does not rely on the unavailable `PipeOptions.CurrentUserOnly` enum member. The child argv binds the pipe name, parent PID, one-case nonce, run, and external manifest fingerprint. The UTF-8 newline-delimited Hello schema is `StorageChronicle.InstallerCaseAuthorizationHello.v1`; it binds RunId, CaseId, PC, child PID, parent PID, nonce, and manifest SHA-256. The parent compares the kernel-reported pipe client PID to the `runas` process PID and validates every Hello field before sending exactly one `StorageChronicle.InstallerCaseAuthorizationGrant.v1` frame with the matching fields, exact phrase, and UTC grant time. A receipt, phrase, or nonce alone is not authorization.

UAC cancellation (native error 1223) is `NOT_EXECUTED`; it creates no case result/log and starts no driver. Other launcher or protocol failures fail closed. If a launched elevated child remains live at timeout, the parent never kills it and marks all later dependent cases `NOT_EXECUTED`. The parent does not inspect child result/log files until that exact child exits. It then requires create-only, case-bound JSON result/log schemas (`StorageChronicle.InstallerCaseResult.v1` and `StorageChronicle.InstallerCaseLog.v1`) matching PC, RunId, and CaseId, and validates result assertions and evidence. Missing, corrupt, mismatched, or passing-with-nonzero-exit results cannot pass.

## Invariants and failure behavior

- All eleven case IDs remain in the aggregate manifest; missing prerequisites and UAC cancellation never count as success.
- `EnableLUA` must equal 1 and the physical parent must not be an administrator. The typed phrase is an intent confirmation, not a substitute for Windows UAC.
- Only one elevated case process is launched at a time. There is no stdin phrase forwarding, inherited nonce authorization, fallback direct-driver path, or automatic termination of a timed-out elevated child.
- Child result and log destinations must not exist before launch; the driver is responsible for create-only output. Existing evidence is never overwritten.
- A successful case needs the matching physical target declaration, passing assertions, and existing host-visible evidence. This harness does not treat an exit code alone as proof.

## Dependencies, tests, and limitations

Depends on Windows PowerShell 5.1, `NamedPipeServerStreamAcl`, Windows UAC/RunAs, the matching independently audited driver, a reviewed hash manifest, and an explicitly prepared dedicated PC/fixture. Automated protocol tests cover exact Hello matching, mismatches, nonce replay, name collision behavior, UAC-cancel mapping, and static orchestration order. PowerShell AST parsing and an ACL-backed pipe create/inspect test cover the local Windows PowerShell 5.1 API shape. Those checks do not execute UAC, prove the interactive prompt, audit all MSI side effects, or close the MSI hash-to-use race. The physical installer remains **DO NOT RUN** until the separate audit, protected payload staging, and acceptance gates pass.
