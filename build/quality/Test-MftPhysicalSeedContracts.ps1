[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $PSScriptRoot 'MftPhysicalSeed.Contracts.ps1')

$runId = '8d9b1f4c-2976-4e19-9fe0-0caa99b24f01'
$seedPath = Join-Path (Join-Path ([IO.Path]::GetTempPath()) $runId) 'MFT-seed.vhdx'
$inventory = [pscustomobject]@{
    IsWindows = $true; IsPhysicalMachine = $true; OperatingSystem = 'Microsoft Windows 11 Pro'; OsBuild = '26100'
    CpuName = 'Contract CPU'; CpuLogicalCount = 8; MemoryMiB = 16384; SystemDrive = 'C:'
    DevicePath = '\\.\D:'; MarkerPath = 'D:\.StorageChronicle\MftSeed.json'; ProtectedVolumeUniqueIds = @()
    Seed = [pscustomobject]@{ Path = $seedPath; Attached = $true; VhdFormat = 'VHDX'; VhdType = 'Dynamic'; SizeBytes = 64GB; DiskNumber = 12; RunId = $runId }
    Disk = [pscustomobject]@{ Number = 12; UniqueId = 'disk-id-contract'; BusType = 'File Backed Virtual'; OperationalStatus = 'Online'; IsSystem = $false; IsBoot = $false }
    Volume = [pscustomobject]@{ UniqueId = 'volume-id-contract'; VolumeGuidPath = '\\?\Volume{3f8c9012-4552-4e2d-a5b9-0caa99b24f01}\'; DriveLetter = 'D'; DriveRoot = 'D:\'; FileSystem = 'NTFS'; FileSystemLabel = 'SC_TEST_MFT_VOLUME'; IsSystem = $false; IsBoot = $false; IsRecovery = $false; IsPageFile = $false; IsCrashDump = $false }
    Marker = [pscustomobject]@{ Schema = 'StorageChronicle.MftSeedMarker.v1'; Role = 'MftSeed'; RunId = $runId; VhdxPath = $seedPath; DiskUniqueId = 'disk-id-contract'; VolumeUniqueId = 'volume-id-contract'; VolumeGuidPath = '\\?\Volume{3f8c9012-4552-4e2d-a5b9-0caa99b24f01}\'; VolumeLabel = 'SC_TEST_MFT_VOLUME'; FileSystem = 'NTFS'; DatasetEntryCount = 1000000 }
}

$passed = 0
$failed = [System.Collections.Generic.List[string]]::new()
function Invoke-ContractCase([string]$Name, [scriptblock]$Body) {
    try { & $Body; $script:passed++; Write-Host "PASS $Name" }
    catch { $script:failed.Add("$Name`: $($_.Exception.Message)"); Write-Host "FAIL $Name" -ForegroundColor Red }
}
function Assert-Refused([string]$Name, [scriptblock]$Mutation) {
    Invoke-ContractCase $Name {
        $case = $script:inventory | ConvertTo-Json -Depth 8 | ConvertFrom-Json
        & $Mutation $case
        try { Assert-MftPhysicalSeedInventory $case | Out-Null; throw 'Unsafe MFT seed inventory was accepted.' }
        catch { if ($_.Exception.Message -eq 'Unsafe MFT seed inventory was accepted.') { throw } }
    }
}

Invoke-ContractCase 'valid physical dynamic VHDX identities pass' {
    $result = Assert-MftPhysicalSeedInventory $inventory
    if ($result.DatasetEntryCount -ne 1000000 -or $result.DiskUniqueId -ne 'disk-id-contract') { throw 'Verified identity was not retained in evidence.' }
}
Invoke-ContractCase 'full MFT runner uses physical host preflight and remains blocked without seed provenance' {
    $runner = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build/quality/Test-FullBenchmarkMatrix.ps1') -Raw
    if ($runner.Contains('Windows 11 TestLab guest') -or $runner.Contains('STORAGE_CHRONICLE_MFT_VM_CPU_COUNT') -or $runner.Contains('STORAGE_CHRONICLE_MFT_VM_MEMORY_MIB')) { throw 'The full matrix still depends on guest/VM evidence.' }
    if (-not $runner.Contains('no audited create-new VHDX/seed workflow') -or -not $runner.Contains('Write-NotExecuted')) { throw 'The matrix does not remain fail-closed without seed-creation provenance.' }
    $finalGate = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build/quality/Test-FinalAcceptance.ps1') -Raw
    foreach ($field in @('DiskNumber', 'DiskUniqueId', 'VolumeUniqueId', 'VolumeGuidPath', 'DevicePath', 'MarkerPath', 'SeedRunId', 'DatasetEntryCount', 'CpuLogicalCount', 'MemoryMiB', 'VhdxSizeGiB')) {
        if (-not $finalGate.Contains($field)) { throw "Final acceptance does not require physical seed field $field." }
    }
}
Assert-Refused 'virtual machine host rejected' { param($x) $x.IsPhysicalMachine = $false }
Assert-Refused 'C drive MFT target rejected' { param($x) $x.DevicePath = '\\.\C:'; $x.Volume.DriveLetter = 'C' }
Assert-Refused 'unattached VHDX rejected' { param($x) $x.Seed.Attached = $false }
Assert-Refused 'fixed VHDX rejected' { param($x) $x.Seed.VhdType = 'Fixed' }
Assert-Refused 'disk identity mismatch rejected' { param($x) $x.Disk.UniqueId = 'different-disk' }
Assert-Refused 'VHDX attached to a different disk number rejected' { param($x) $x.Seed.DiskNumber = 13 }
Assert-Refused 'physical USB disk rejected' { param($x) $x.Disk.BusType = 'USB' }
Assert-Refused 'boot disk rejected' { param($x) $x.Disk.IsBoot = $true }
Assert-Refused 'volume identity mismatch rejected' { param($x) $x.Volume.VolumeGuidPath = '\\?\Volume{different}\' }
Assert-Refused 'volume unique ID mismatch rejected' { param($x) $x.Volume.UniqueId = 'different-volume' }
Assert-Refused 'device path and volume letter mismatch rejected' { param($x) $x.DevicePath = '\\.\E:' }
Assert-Refused 'system volume rejected' { param($x) $x.Volume.IsSystem = $true }
Assert-Refused 'pagefile volume rejected' { param($x) $x.ProtectedVolumeUniqueIds = @('volume-id-contract') }
Assert-Refused 'crash dump volume rejected' { param($x) $x.Volume.IsCrashDump = $true }
Assert-Refused 'recovery volume rejected' { param($x) $x.Volume.IsRecovery = $true }
Assert-Refused 'wrong volume label rejected' { param($x) $x.Volume.FileSystemLabel = 'DATA' }
Assert-Refused 'VHDX path mismatch with persistent marker rejected' { param($x) $x.Marker.VhdxPath = 'C:\other\seed.vhdx' }
Assert-Refused 'marker must remain on seed volume' { param($x) $x.MarkerPath = 'C:\MftSeed.json' }
Assert-Refused 'stale marker run GUID rejected' { param($x) $x.Marker.RunId = 'not-a-guid' }
Assert-Refused 'undersized MFT dataset rejected' { param($x) $x.Marker.DatasetEntryCount = 999999 }

Write-Output "MftPhysicalSeedContracts Passed=$passed Failed=$($failed.Count)"
if ($failed.Count -gt 0) { $failed | ForEach-Object { Write-Error $_ }; exit 1 }
