# New-TestDataVhdx.ps1

Creates and attaches a new disposable data VHDX only under the approved TestLab root and only to `SC-Test-W11` or `SC-Test-W10`. `Workload` defaults to 4 GiB; `Mft` requires at least 16 GiB. The operation is preview-only until explicit `-Apply` is supplied and never reuses an existing VHDX.

The manifest carries a marker contract for the guest workload: schema, test ID, role, and VHDX identity. Guest initialization must write that marker before any destructive mutation. The script never targets a host physical disk or a system/boot/pagefile/crashdump volume.
