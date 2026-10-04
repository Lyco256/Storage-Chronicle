# Test-MftPhysicalSeedContracts.ps1

Runs non-privileged in-memory contract cases for physical MFT seed identity validation. It covers a valid inventory and rejection of VM hosts, C: targets, unattached/fixed VHDX, disk and volume identity mismatches, system/pagefile volume roles, wrong filesystem label, invalid run GUID, and fewer than one million declared entries. Static assertions ensure the full runner no longer requires TestLab guest/VM fields, remains blocked without seed-creation provenance, and the final acceptance gate checks the physical identity evidence.

It does not query host disks, create/attach/format VHDX, start a product or benchmark, or claim physical acceptance. `build/quality/Test-Quality.ps1` includes this contract suite.
