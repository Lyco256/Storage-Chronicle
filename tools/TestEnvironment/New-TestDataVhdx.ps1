[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('SC-Test-W11-VBox', 'SC-Test-W10-VBox')][string]$VmName,
    [ValidateSet('Workload', 'Mft', 'NonNtfs', 'AclDenied')][string]$Role = 'Workload',
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
$manifestPath = Join-Path $artifactDirectory "data-vbox-$safeTestId.json"
$manifest = [ordered]@{ Schema = 'StorageChronicle.VirtualBoxTestDataDisk.v1'; VmName = $VmName; Role = $Role; TestId = $TestId; Apply = [bool]$Apply; Status = 'NOT_EXECUTED'; StartedUtc = [DateTimeOffset]::UtcNow; AcceptanceEligible = $false }
$created = $false
try {
    $config = Get-TestLabConfig -ConfigPath $ConfigPath
    $root = Assert-TestLabRoot -Root $config.Root
    $vm = Assert-ExactTestLabVm -Name $VmName
    Assert-TestLabVmDisks -Vm $vm -Root $root
    Assert-TestLabVmProfile -Name $VmName -Root $root | Out-Null
    if ([string]$vm.State -ne 'poweroff') { throw "The VirtualBox VM must be powered off before attaching a disposable data disk: $VmName" }
    if ($SizeGiB -le 0) { $SizeGiB = if ($Role -eq 'Mft') { 16 } else { 4 } }
    if ($Role -eq 'Mft' -and $SizeGiB -lt 16) { throw 'The MFT TestLab data disk must be at least 16 GiB.' }
    if ($Role -eq 'NonNtfs' -and $SizeGiB -lt 4) { throw 'The non-NTFS TestLab data disk must be at least 4 GiB.' }
    $path = Assert-PathUnderRoot -Root $root -Path (Join-Path $root "data\$VmName\$safeTestId-$Role.vdi")
    if (-not $Apply) { $manifest.Status = 'READY_FOR_USER_APPLY'; $manifest.DiskPath = $path; $manifest.SizeGiB = $SizeGiB; $manifest.Reason = 'Re-run with -Apply only after approving creation and attachment of this disposable VirtualBox dynamic disk.'; Write-TestLabJson $manifestPath $manifest; Write-Output ($manifest | ConvertTo-Json -Depth 10); exit 2 }
    if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite an existing disposable disk: $path" }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    Invoke-VBoxManage @('createmedium', 'disk', '--filename', $path, '--size', ([int]($SizeGiB * 1024)), '--format', 'VDI', '--variant', 'Standard') | Out-Null
    $created = $true
    Invoke-VBoxManage @('storageattach', $VmName, '--storagectl', 'SATA', '--port', '1', '--device', '0', '--type', 'hdd', '--medium', $path) | Out-Null
    $manifest.Status = 'CREATED_AND_ATTACHED'; $manifest.AcceptanceEligible = $true; $manifest.DiskPath = $path; $manifest.DiskFormat = 'VDI dynamic'; $manifest.SizeGiB = $SizeGiB; $manifest.Marker = [ordered]@{ FileName = '.storage-chronicle-testlab-marker.json'; VolumeMarkerFileName = 'StorageChronicleTestVolume.json'; Schema = 'StorageChronicle.TestLabDataMarker.v1'; TestId = $TestId; Role = $Role; VolumeLabel = if ($Role -eq 'Mft') { 'SC_TEST_MFT_VOLUME' } elseif ($Role -eq 'NonNtfs') { 'SC_TEST_NONNTFS_VOLUME' } else { 'SC_TEST_VOLUME' }; FileSystem = if ($Role -eq 'NonNtfs') { 'exFAT' } else { 'NTFS' } }
    Write-TestLabJson $manifestPath $manifest; Write-Output ($manifest | ConvertTo-Json -Depth 10); exit 0
}
catch {
    $manifest.Status = 'BLOCKED'; $manifest.Error = $_.Exception.Message
    if ($Apply -and $created -and (Test-Path -LiteralPath $path -PathType Leaf)) { try { Invoke-VBoxManage @('storageattach', $VmName, '--storagectl', 'SATA', '--port', '1', '--device', '0', '--type', 'hdd', '--medium', 'none') -AllowNonZero | Out-Null; Remove-Item -LiteralPath $path -Force } catch { $manifest.CleanupError = $_.Exception.Message } }
    $manifest.HumanHandoff = [ordered]@{ Blocked = 'VirtualBox disposable data disk'; Reason = $_.Exception.Message; WhyUserActionIsRequired = 'Disk attachment is a host/guest boundary operation and must remain explicitly approved.'; DoThis = @('Approve only the disposable disk path under TestLabRoot.', 'Keep the selected VM powered off before retrying.'); ExpectedResult = 'Status=CREATED_AND_ATTACHED and the disk path remains under TestLabRoot.'; DoNotDo = @('Do not attach raw physical disks or the host C: drive.', 'Do not format a disk number not returned for this named VirtualBox disk.'); ResumeCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`" -VmName $VmName -Role $Role -TestId $TestId -ConfigPath `"$ConfigPath`" -Apply"; SendBack = @('Status, Error, VM name, DiskPath, and marker fields only; no credentials.') }
    Write-TestLabJson $manifestPath $manifest; Write-Error $_.Exception.Message; exit 2
}
