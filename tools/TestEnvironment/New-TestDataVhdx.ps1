[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('SC-Test-W11', 'SC-Test-W10')][string]$VmName,
    [ValidateSet('Workload', 'Mft', 'NonNtfs')][string]$Role = 'Workload',
    [string]$TestId = ([guid]::NewGuid().ToString('N')),
    [int]$SizeGiB,
    [string]$ConfigPath,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestLab.Common.ps1')
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactDirectory = New-TestLabArtifactDirectory -RepositoryRoot $repositoryRoot
$safeTestId = $TestId -replace '[^A-Za-z0-9_.-]', '-'
$manifestPath = Join-Path $artifactDirectory "data-vhdx-$safeTestId.json"
$manifest = [ordered]@{ Schema = 'StorageChronicle.TestDataVhdx.v1'; VmName = $VmName; Role = $Role; TestId = $TestId; Apply = [bool]$Apply; Status = 'NOT_EXECUTED'; StartedUtc = [DateTimeOffset]::UtcNow }
try {
    $config = Get-TestLabConfig -ConfigPath $ConfigPath
    $root = Assert-TestLabRoot -Root $config.Root
    if ($SizeGiB -le 0) { $SizeGiB = if ($Role -eq 'Mft') { 16 } else { 4 } }
    if ($Role -eq 'Mft' -and $SizeGiB -lt 16) { throw 'The MFT benchmark VHDX must be at least 16 GiB.' }
    if ($Role -eq 'NonNtfs' -and $SizeGiB -lt 4) { throw 'The non-NTFS test VHDX must be at least 4 GiB.' }
    $path = Assert-PathUnderRoot -Root $root -Path (Join-Path $root "data\$VmName\$safeTestId-$Role.vhdx")
    if (-not $Apply) {
        $manifest.Status = 'READY_FOR_USER_APPLY'
        $manifest.VhdxPath = $path
        $manifest.SizeGiB = $SizeGiB
        $manifest.Reason = 'Re-run with -Apply only after approving creation and attachment of this disposable data VHDX.'
        Write-TestLabJson -Path $manifestPath -Value $manifest
        Write-Output ($manifest | ConvertTo-Json -Depth 10)
        exit 2
    }
    Assert-HyperVMutationPrerequisites
    $null = Assert-ExactTestLabVm -Name $VmName
    if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite an existing data VHDX: $path" }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    New-VHD -Path $path -Dynamic -SizeBytes ($SizeGiB * 1GB) | Out-Null
    Add-VMHardDiskDrive -VMName $VmName -Path $path
    $manifest.Status = 'CREATED_AND_ATTACHED'
    $manifest.VhdxPath = $path
    $manifest.SizeGiB = $SizeGiB
    $manifest.Marker = [ordered]@{ FileName = '.storage-chronicle-testlab-marker.json'; VolumeMarkerFileName = 'StorageChronicleTestVolume.json'; Schema = 'StorageChronicle.TestLabDataMarker.v1'; TestId = $TestId; Role = $Role; VolumeLabel = if ($Role -eq 'Mft') { 'SC_TEST_MFT_VOLUME' } elseif ($Role -eq 'NonNtfs') { 'SC_TEST_NONNTFS_VOLUME' } else { 'SC_TEST_VOLUME' }; FileSystem = if ($Role -eq 'NonNtfs') { 'exFAT' } else { 'NTFS' }; VhdxPath = $path }
    Write-TestLabJson -Path $manifestPath -Value $manifest
    Write-Output ($manifest | ConvertTo-Json -Depth 10)
    exit 0
}
catch {
    $manifest.Status = 'FAILED'
    $manifest.Error = $_.Exception.Message
    Write-TestLabJson -Path $manifestPath -Value $manifest
    Write-Error $_.Exception.Message
    exit 1
}
