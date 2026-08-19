# Test-TestLabPrerequisites.ps1

Performs the read-only host preflight required before Hyper-V TestLab construction. It records Windows edition/build/architecture, virtualization and SLAT capability, Hyper-V module and service availability, administrator state, memory and candidate-volume space, and user-supplied ISO/root paths. It never enables Windows features, changes firmware/BCD, restarts the host, downloads media, formats disks, or creates a VM. Missing user approval, unsupported editions, absent Hyper-V, and omitted ISO/root values remain explicit blocking statuses and produce exit code 2.

## Role

This script is the first safety boundary for destructive acceptance operations. Later TestLab scripts must consume its successful result and independently enforce VHDX marker and system-volume guards.

## Inputs and outputs

Inputs are optional user-approved paths and resource thresholds. Output is JSON on stdout and, when requested, an explicitly selected artifact path. No credentials, license keys, file contents, or content hashes are read or stored.

## Failure behavior

The script fails closed with `ReadyForTestLab=false` and exit code 2 for any missing prerequisite, unsupported host, missing user action, or incomplete inspection. An administrator rerun is required where Windows feature state cannot be queried.

## Tests and verification

Run with an approved candidate root and official ISO paths only after the user has selected them. The script is validated by PowerShell syntax checking and manual read-only execution; it is not a substitute for the later VM acceptance artifacts.
