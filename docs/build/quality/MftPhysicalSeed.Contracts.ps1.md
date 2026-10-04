# MftPhysicalSeed.Contracts.ps1

Provides read-only MFT seed inventory and fail-closed identity validation for the Windows full benchmark lane. `Get-MftPhysicalSeedInventory` queries the attached VHDX, disk, partitions, volumes, host OS/CPU/memory, active and configured pagefile paths, crash-dump configuration, and persistent marker. It does not create, attach, detach, format, initialize, or modify disks, volumes, marker files, registry state, or VHDX files. `Assert-MftPhysicalSeedInventory` cross-checks device path, attached dynamic VHDX, disk number/unique ID, volume unique ID/GUID/NTFS label, marker run GUID/path, protected-volume roles, and declared seed size.

The reusable validator is exercised with in-memory inventories by `Test-MftPhysicalSeedContracts.ps1`; its successful contract fixture does not claim physical evidence. Unknown/missing data and identity conflicts throw and keep acceptance blocked. Read-only PowerShell Storage, Hyper-V, CIM, and registry queries are Windows-only dependencies.

This module intentionally does not prove that the VHDX/seed was created new within an approved isolation root. Until an audited create-new seed workflow supplies independent provenance, `Test-FullBenchmarkMatrix.ps1 -IncludeMft` records `NOT_EXECUTED` after preflight and does not launch BenchmarkDotNet.
