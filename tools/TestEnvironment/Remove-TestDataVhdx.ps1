[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('SC-Test-W11', 'SC-Test-W10')][string]$VmName,
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
$manifest = [ordered]@{ Schema = 'StorageChronicle.RemoveTestDataVhdx.v1'; VmName = $VmName; VhdxPath = $VhdxPath; Apply = [bool]$Apply; Status = 'NOT_EXECUTED'; StartedUtc = [DateTimeOffset]::UtcNow }
$manifestPath = Join-Path $artifactDirectory 'remove-data-vhdx.json'
try {
    $config = Get-TestLabConfig -ConfigPath $ConfigPath
    $root = Assert-TestLabRoot -Root $config.Root
    $path = Assert-PathUnderRoot -Root $root -Path $VhdxPath
    if ([IO.Path]::GetExtension($path) -ine '.vhdx') { throw 'Only a .vhdx data path may be removed.' }
    $safeTestId = $TestId -replace '[^A-Za-z0-9_.-]', '-'
    $expectedPath = [IO.Path]::GetFullPath((Join-Path $root "data\$VmName\$safeTestId-$Role.vhdx"))
    if (-not $path.Equals($expectedPath, [StringComparison]::OrdinalIgnoreCase)) { throw "The disposable VHDX path does not match the current TestId/Role marker contract: $path" }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Data VHDX does not exist: $path" }
    if (-not $Apply) {
        $manifest.Status = 'READY_FOR_USER_APPLY'
        $manifest.Reason = 'Re-run with -Apply only after approving detachment and deletion of this disposable VHDX.'
        Write-TestLabJson -Path $manifestPath -Value $manifest
        Write-Output ($manifest | ConvertTo-Json -Depth 10)
        exit 2
    }
    Assert-HyperVMutationPrerequisites
    $null = Assert-ExactTestLabVm -Name $VmName
    $attached = @(Get-VMHardDiskDrive -VMName $VmName | Where-Object { [IO.Path]::GetFullPath($_.Path) -eq $path })
    if ($attached.Count -ne 1) { throw "The VHDX must be attached exactly once to the selected TestLab VM before removal." }
    Remove-VMHardDiskDrive -VMHardDiskDrive $attached[0] -Confirm:$false
    Remove-Item -LiteralPath $path -Force
    $manifest.Status = 'REMOVED'
    $manifest.RemovedUtc = [DateTimeOffset]::UtcNow
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
