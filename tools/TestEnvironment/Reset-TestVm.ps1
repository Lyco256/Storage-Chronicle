[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('SC-Test-W11', 'SC-Test-W10')][string]$Name,
    [string]$CheckpointName = 'StorageChronicle-Baseline',
    [string]$ConfigPath,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestLab.Common.ps1')
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactDirectory = New-TestLabArtifactDirectory -RepositoryRoot $repositoryRoot
$manifestPath = Join-Path $artifactDirectory "reset-$Name.json"
$manifest = [ordered]@{ Schema = 'StorageChronicle.TestLabReset.v1'; Name = $Name; Checkpoint = $CheckpointName; Apply = [bool]$Apply; Status = 'NOT_EXECUTED'; StartedUtc = [DateTimeOffset]::UtcNow }
try {
    $config = Get-TestLabConfig -ConfigPath $ConfigPath
    $root = Assert-TestLabRoot -Root $config.Root
    $null = Assert-PathUnderRoot -Root $root -Path (Join-Path $root $Name)
    if (-not $Apply) {
        $manifest.Status = 'READY_FOR_USER_APPLY'
        $manifest.Reason = 'Re-run with -Apply only when discarding the VM runtime state is approved.'
        Write-TestLabJson -Path $manifestPath -Value $manifest
        Write-Output ($manifest | ConvertTo-Json -Depth 10)
        exit 2
    }
    Assert-HyperVMutationPrerequisites
    $vm = Assert-ExactTestLabVm -Name $Name
    $checkpoint = Get-VMSnapshot -VMName $Name -Name $CheckpointName -ErrorAction SilentlyContinue
    if ($null -eq $checkpoint) { throw "The required baseline checkpoint is missing: $CheckpointName" }
    if ($vm.State -ne 'Off') { Stop-VM -Name $Name -TurnOff -Confirm:$false }
    Restore-VMSnapshot -VMSnapshot $checkpoint -Confirm:$false
    $manifest.Status = 'RESET'
    $manifest.RestoredUtc = [DateTimeOffset]::UtcNow
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
