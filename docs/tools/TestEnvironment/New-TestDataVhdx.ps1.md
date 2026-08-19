# New-TestDataVhdx.ps1

Creates and attaches a new disposable data VHDX only under the approved TestLab root and only to `SC-Test-W11` or `SC-Test-W10`. `Workload` and `NonNtfs` default to 4 GiB; `Mft` requires at least 16 GiB. The operation is preview-only until explicit `-Apply` is supplied and never reuses an existing VHDX.

The manifest carries the guest marker contract for both `.storage-chronicle-testlab-marker.json` and the required `StorageChronicleTestVolume.json`, including schema, test ID, role, volume label, filesystem, and VHDX identity. Guest initialization must write both markers before any destructive mutation. If attachment or manifest recording fails after creation, the catch path rolls back only that exact newly-created VHDX and records any rollback failure. The script never targets a host physical disk or a system/boot/pagefile/crashdump volume.
