# Test-TestLabPrerequisites.ps1

Performs the read-only VirtualBox host preflight required before TestLab construction. It records Windows edition/build/architecture, CPU/core/RAM/free-space/NTFS status, TPM and Device Guard/Memory Integrity audit values, WMI virtualization, VirtualBox version/path/hostinfo, current approved VM and baseline state, Hyper-V presence only as an audit field, and user-supplied ISO/root paths. It never enables features, changes firmware/BCD, restarts the host, downloads media, formats disks, or creates a VM. An unavailable `VBoxManage.exe`, insufficient RAM, missing ISO, unsafe root, or unavailable hardware capability remains explicit and produces exit code 2.

## Role

This script is the first safety boundary for destructive acceptance operations. Later TestLab scripts must consume its successful result and independently enforce VHDX marker and system-volume guards.

## Inputs and outputs

Inputs are optional user-approved paths and resource thresholds. Output is JSON on stdout and, when requested, an explicitly selected artifact path. No credentials, license keys, file contents, or content hashes are read or stored.

## Failure behavior

The script fails closed with `ReadyForProvisioning=false`, `ReadyForFunctionalAcceptance=false`, and exit code 2 for any missing prerequisite, unsupported host, missing user action, or incomplete inspection. A UAC-only Windows feature query is recorded as `REQUIRES_USER_ACTION` audit information and does not abort the normal-user preflight. Human-controlled installation, firmware, ISO, and guest setup use the fixed handoff fields `Blocked`, `Reason`, `WhyUserActionIsRequired`, `DoThis`, `ExpectedResult`, `DoNotDo`, `ResumeCommand`, and `SendBack`.

## Tests and verification

Run with an approved candidate root and official ISO paths only after the user has selected them. The script is validated by PowerShell syntax checking and manual read-only execution; it is not a substitute for the later VM acceptance artifacts.
