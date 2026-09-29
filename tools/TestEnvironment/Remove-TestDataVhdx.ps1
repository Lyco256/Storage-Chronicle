[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('SC-Test-W11-VBox', 'SC-Test-W10-VBox')][string]$VmName,
    [Parameter(Mandatory = $true)][string]$VhdxPath,
    [Parameter(Mandatory = $true)][string]$TestId,
    [Parameter(Mandatory = $true)][ValidateSet('Workload', 'Mft', 'NonNtfs', 'AclDenied')][string]$Role,
    [string]$ConfigPath,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestLab.Common.ps1')
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactDirectory = New-TestLabArtifactDirectory -RepositoryRoot $repositoryRoot
$manifestPath = Join-Path $artifactDirectory 'remove-data-vbox.json'
$manifest = [ordered]@{ Schema = 'StorageChronicle.RemoveVirtualBoxTestDataDisk.v1'; VmName = $VmName; DiskPath = $VhdxPath; Apply = [bool]$Apply; Status = 'NOT_EXECUTED'; StartedUtc = [DateTimeOffset]::UtcNow; AcceptanceEligible = $false }
try {
    $config = Get-TestLabConfig -ConfigPath $ConfigPath
    $root = Assert-TestLabRoot -Root $config.Root
    $path = Assert-PathUnderRoot -Root $root -Path $VhdxPath
    if ([IO.Path]::GetExtension($path) -ine '.vdi') { throw 'Only the VirtualBox .vdi disposable data disk may be removed by this helper.' }
    $safeTestId = $TestId -replace '[^A-Za-z0-9_.-]', '-'
    $expectedPath = [IO.Path]::GetFullPath((Join-Path $root "data\$VmName\$safeTestId-$Role.vdi"))
    if (-not $path.Equals($expectedPath, [StringComparison]::OrdinalIgnoreCase)) { throw "The disposable disk path does not match the current TestId/Role contract: $path" }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Disposable VirtualBox disk does not exist: $path" }
    $vm = Assert-ExactTestLabVm -Name $VmName
    Assert-TestLabVmDisks -Vm $vm -Root $root
    Assert-TestLabVmProfile -Name $VmName -Root $root | Out-Null
    if ([string]$vm.State -ne 'poweroff') { throw "The VirtualBox VM must be powered off before disk removal: $VmName" }
    if (-not $Apply) { $manifest.Status = 'READY_FOR_USER_APPLY'; $manifest.Reason = 'Re-run with -Apply only after approving detachment and deletion of this disposable disk.'; Write-TestLabJson $manifestPath $manifest; Write-Output ($manifest | ConvertTo-Json -Depth 10); exit 2 }
    $paths = @(Get-VBoxVmDiskPaths $VmName)
    if ($paths -notcontains $path) { throw "The disposable disk is not attached exactly as expected to ${VmName}: $path" }
    Invoke-VBoxManage @('storageattach', $VmName, '--storagectl', 'SATA', '--port', '1', '--device', '0', '--type', 'hdd', '--medium', 'none') | Out-Null
    Remove-Item -LiteralPath $path -Force
    $manifest.Status = 'REMOVED'; $manifest.AcceptanceEligible = $true; $manifest.RemovedUtc = [DateTimeOffset]::UtcNow
    Write-TestLabJson $manifestPath $manifest; Write-Output ($manifest | ConvertTo-Json -Depth 10); exit 0
}
catch {
    $manifest.Status = 'BLOCKED'; $manifest.Error = $_.Exception.Message; Write-TestLabJson $manifestPath $manifest; Write-Error $_.Exception.Message; exit 2
}
