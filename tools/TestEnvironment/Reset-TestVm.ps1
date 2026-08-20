[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][ValidateSet('SC-Test-W11-VBox', 'SC-Test-W10-VBox')][string]$Name,
    [string]$SnapshotName = 'SC-CLEAN-BASELINE',
    [string]$ConfigPath,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'TestLab.Common.ps1')
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactDirectory = New-TestLabArtifactDirectory -RepositoryRoot $repositoryRoot
$manifestPath = Join-Path $artifactDirectory 'reset-testlab-vm.json'
$manifest = [ordered]@{ Schema = 'StorageChronicle.VirtualBoxTestLabReset.v1'; Name = $Name; Snapshot = $SnapshotName; Apply = [bool]$Apply; Status = 'NOT_EXECUTED'; StartedUtc = [DateTimeOffset]::UtcNow; AcceptanceEligible = $false }
try {
    $config = Get-TestLabConfig -ConfigPath $ConfigPath
    $root = Assert-TestLabRoot $config.Root
    $vm = Assert-ExactTestLabVm $Name
    Assert-TestLabVmDisks -Vm $vm -Root $root
    Ensure-TestLabBaseline -Name $Name -Snapshot $SnapshotName
    if (-not $Apply) { $manifest.Status = 'READY_FOR_USER_APPLY'; $manifest.Reason = 'The approved baseline exists; re-run with -Apply to restore it.'; Write-TestLabJson $manifestPath $manifest; Write-Output ($manifest | ConvertTo-Json -Depth 10); exit 2 }
    if ($PSCmdlet.ShouldProcess($Name, 'Restore VirtualBox baseline')) { Restore-TestLabBaseline -Name $Name -Snapshot $SnapshotName }
    $manifest.Status = 'RESTORED'; $manifest.AcceptanceEligible = $true; $manifest.CompletedUtc = [DateTimeOffset]::UtcNow
    Write-TestLabJson $manifestPath $manifest; Write-Output ($manifest | ConvertTo-Json -Depth 10); exit 0
}
catch {
    $manifest.Status = 'BLOCKED'; $manifest.Error = $_.Exception.Message; $manifest.HumanHandoff = [ordered]@{ Blocked = 'VirtualBox baseline restore'; Reason = $_.Exception.Message; WhyUserActionIsRequired = 'A missing baseline or VM setup cannot be safely synthesized by the normal host process.'; DoThis = @('Complete guest setup and create exactly one SC-CLEAN-BASELINE snapshot if it is absent.', 'Keep the VM powered off before retrying the restore.'); ExpectedResult = 'Status=RESTORED and the VM remains powered off at the clean baseline.'; DoNotDo = @('Do not restore arbitrary snapshots or attach disks outside TestLabRoot.'); ResumeCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`" -Name $Name -SnapshotName $SnapshotName -ConfigPath `"$ConfigPath`" -Apply"; SendBack = @('Status, Error, VM name, and snapshot name only; no credentials.') }; Write-TestLabJson $manifestPath $manifest; Write-Error $_.Exception.Message; Write-Output ($manifest | ConvertTo-Json -Depth 10); exit 2
}
