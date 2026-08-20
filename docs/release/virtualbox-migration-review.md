# VirtualBox migration review

Review scope: Requirements 32–36, `TOP_CODEX.md`, the repository requirements, and the implementation on `feat/testlab-virtualbox-migration`. This review is an implementation and audit record; it is not a claim that the Windows acceptance matrix passed.

## Provider migration

- `tools/TestEnvironment/VirtualBox.Common.ps1` is the only VirtualBox control boundary. It owns VBoxManage discovery/process capture, exact VM names, disk-root validation, 4096 MiB/2-vCPU settings, EFI/TPM2, disabled 3D/audio/USB/clipboard/drag-and-drop, provisioning NAT versus acceptance-time offline state, baseline restore, guest readiness, guestcontrol, resource gates, and artifact transfer.
- The exact VM identities are `SC-Test-W11-VBox` and `SC-Test-W10-VBox`. OS disks are dynamic VDI files under the approved TestLabRoot; only one VM is started at a time. The guest-internal destructive VHDX contract remains in `build/Test-Privileged.ps1` and now uses DiskPart plus Windows Storage cmdlets, preserving `-CreateVhdx`, `-VhdxPath`, `-VhdxRoot`, marker, TestId, role, and acceptance evidence contracts.
- Generic TestLab command/copy/reset/initialization scripts, Windows 10 Stage A, installer matrix, final gate, correlation identity, and hot-attach expectations were migrated. The removed Hyper-V runtime entry points are `Run-HyperVInstallerAcceptance.ps1` and `Invoke-HyperVInstallerCase.ps1`; no compatibility shim remains.
- Existing product event, source/canonical/state, MFT/USN/ETW/Service/SMB, correlation, benchmark, quiet-witness, resource, marker, and acceptance-contract logic was retained. No product assembly references VBoxManage and no substitute shared contract was introduced.

## Safety and resource audit

- Host control remains non-administrator. UAC, firmware, ISO selection, VirtualBox installation, Guest Additions, Windows setup, guest credential creation, and baseline snapshot creation remain explicit user actions.
- Raw disks, host C:, shared folders, clipboard, drag-and-drop, USB, webcam, audio, and network credentials are not used. NAT is allowed only for provisioning; acceptance start requires NIC `none`. A baseline is restored before each installer case and before each TestLab workload.
- The resource gate requires at least 6 GiB available host RAM and 40 GiB free on TestLabRoot; MFT seed creation requires 60 GiB. Required workload sizes/counts remain unchanged, including 10K/100K/1M MFT profiles; the implementation only serializes VM execution and reuses a clean seed/baseline.

## Host preflight evidence

The latest read-only normal-user preflight was run on 2026-08-20 against `C:\Temp` only as an existing candidate root. It returned exit code 2 and `Status=BLOCKED`; no VM or host mutation was attempted.

| Field | Observed value |
| --- | --- |
| OS/build | Windows 11 Home, `10.0.26200`, x64 |
| CPU / logical processors | 12th Gen Intel Core i5-1235U / 12 |
| Total / available RAM | 15.83 GiB / 2.67 GiB (below the required 6 GiB) |
| Candidate root | `C:\Temp`, NTFS, 171.05 GiB free |
| WMI firmware virtualization | `false`; not treated as the sole blocker |
| Device Guard / Memory Integrity | detected enabled by the read-only audit |
| TPM | not readable from the normal-user preflight; no TPM status was promoted to success |
| VirtualBox | `VBoxManage.exe` not installed/discoverable; hostinfo unavailable |
| VMs/snapshots | none reported; no ISO paths supplied |
| Result | `ReadyForProvisioning=false`, `ReadyForFunctionalAcceptance=false`, exit 2 |

The preflight used the fixed handoff fields `Blocked`, `Reason`, `WhyUserActionIsRequired`, `DoThis`, `ExpectedResult`, `DoNotDo`, `ResumeCommand`, and `SendBack`. It did not self-elevate or bypass the UAC/security boundary.

## Static residual scan

After migration, the runtime scan over `build`, `tools`, `src`, and `tests` found no Hyper-V VM-control cmdlets, PowerShell Direct, VMMS service control, or old installer entry points. The only Hyper-V-specific runtime text is the requirement-34 audit-only detection of the optional feature/module; it does not enable, control, or depend on Hyper-V. The generic resource classifier also retains virtual-machine vendor detection. Historical requirement/docs and the migration inventory retain old terms only to preserve traceability and are explicitly marked or classified historical.

## Verification performed

- 51 PowerShell files parsed with `System.Management.Automation.Language.Parser`; 0 parser errors.
- `build/quality/Test-Quality.ps1`: exit 0; architecture, integration, coverage, and VirtualBox boundary contract checks passed.
- `StorageChronicle.Installer.Tests.exe`: 8/8 passed.
- `StorageChronicle.LiveCorrelationValidator.Tests.exe`: 4/4 passed, including pass and fail-closed fixtures.
- `StorageChronicle.Platform.Windows.Integration.Tests` Release build: passed with 0 errors.
- The preflight negative path returned exit code 2 with non-eligible JSON.
- `build/quality/Test-VirtualBoxTestLab.ps1`: 6/6 provider-boundary contract cases passed; this does not claim a real guest run.
- `git diff --check` and the final forbidden-control scan remain required immediately before commit/push.

## Not executed / remaining acceptance blockers

The following remain `NOT_EXECUTED` and are not acceptance passes: VirtualBox 7.2.16 installation/hostinfo/start smoke, Windows 11/Windows 10 guest setup, matching Guest Additions, baseline snapshot creation, guest `whoami`/copy round trip, `Test-Privileged -CreateVhdx` inside a Windows guest, the 11-case installer matrices, Stage A, real privileged MFT/USN/ETW/Service/SMB/correlation/resource runs, and final comprehensive acceptance. These require the user-controlled host/guest preparation described by Requirement 34 and real Windows guest evidence.
