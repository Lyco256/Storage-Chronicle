# New-TestDataVhdx.ps1

Creates and attaches a new disposable dynamic VirtualBox VDI only under the approved TestLab root and only to `SC-Test-W11-VBox` or `SC-Test-W10-VBox`. `Workload` and `NonNtfs` default to 4 GiB; `Mft` requires at least 16 GiB. The operation is preview-only until explicit `-Apply` is supplied and never reuses an existing VDI.

The manifest carries the guest marker contract for both `.storage-chronicle-testlab-marker.json` and the required `StorageChronicleTestVolume.json`, including schema, test ID, role, volume label, filesystem, and VDI identity. Guest initialization must write both markers before any destructive mutation. If attachment or manifest recording fails after creation, the catch path rolls back only that exact newly-created VDI and records any rollback failure. The script never targets a host physical disk or a system/boot/pagefile/crashdump volume.
