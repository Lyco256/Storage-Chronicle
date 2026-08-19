[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [string]$GuestWorkloadExecutable,
    [string]$GuestDataRoot = 'D:\StorageChronicleTestData',
    [string]$ExplorerEvidencePath,
    [pscredential]$Credential,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestLab.Common.ps1')
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactDirectory = New-TestLabArtifactDirectory -RepositoryRoot $repositoryRoot -RunId $RunId
$manifestPath = Join-Path $artifactDirectory 'testlab-manifest.json'
$manifest = [ordered]@{
    Schema = 'StorageChronicle.WindowsTestLabExecution.v1'
    RunId = $RunId
    Apply = [bool]$Apply
    Status = 'NOT_EXECUTED'
    AcceptanceEligible = $false
    StartedUtc = [DateTimeOffset]::UtcNow
    Stages = @()
    ArtifactDirectory = $artifactDirectory
}

function Add-Stage([string]$Name, [string]$Status, [string]$Reason, $Evidence = $null) {
    $manifest.Stages += [ordered]@{ Name = $Name; Status = $Status; Reason = $Reason; Evidence = $Evidence }
}

try {
    $config = Get-TestLabConfig -ConfigPath $ConfigPath
    $root = Assert-TestLabRoot -Root $config.Root
    Assert-ExistingIso -Path $config.Windows11Iso -Label 'Windows 11 ISO'
    Assert-ExistingIso -Path $config.Windows10Iso -Label 'Windows 10 22H2 ISO'
    if (-not $Apply) {
        Add-Stage 'preflight' 'READY_FOR_USER_APPLY' 'Configuration and local ISO paths are valid; no VM or guest operation was run.'
        Add-Stage 'windows11' 'NOT_EXECUTED' 'Pass -Apply only after the user approves VM reset, data VHDX creation, and guest mutation.'
        Add-Stage 'windows10' 'NOT_EXECUTED' 'Pass -Apply only after the user approves VM reset, data VHDX creation, and guest mutation.'
        Add-Stage 'explorer-correlation' 'NOT_EXECUTED' 'Real Explorer actions require an interactive human-assisted session and an independent artifact.'
        Write-TestLabJson -Path $manifestPath -Value $manifest
        Write-Output ($manifest | ConvertTo-Json -Depth 12)
        exit 2
    }

    Assert-HyperVMutationPrerequisites
    Add-Stage 'preflight' 'PASSED' 'Hyper-V and VMMS were available on the elevated host.'
    if ([string]::IsNullOrWhiteSpace($GuestWorkloadExecutable)) {
        Add-Stage 'workload' 'NOT_EXECUTED' 'A real guest workload executable path was not supplied.'
    }
    else {
        foreach ($guest in @('Windows11', 'Windows10')) {
            $definition = Get-TestLabVmDefinition -Guest $guest
            $vm = Assert-ExactTestLabVm -Name $definition.Name
            if ($vm.State -ne 'Off') { Stop-VM -Name $definition.Name -TurnOff -Confirm:$false }
            $resetArgs = @{ Name = $definition.Name; ConfigPath = $ConfigPath; Apply = $true }
            & (Join-Path $PSScriptRoot 'Reset-TestVm.ps1') @resetArgs | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Baseline reset failed for $($definition.Name)." }

            $newArgs = @{ VmName = $definition.Name; Role = 'Workload'; TestId = $RunId; ConfigPath = $ConfigPath; Apply = $true }
            $newOutput = @(& (Join-Path $PSScriptRoot 'New-TestDataVhdx.ps1') @newArgs 2>&1)
            if ($LASTEXITCODE -ne 0) { throw "Data VHDX creation failed for $($definition.Name)." }
            $newOutput | Out-File -LiteralPath (Join-Path $artifactDirectory "$($definition.Name)-new-vhdx.log") -Encoding UTF8

            $guestCommand = "& '$GuestWorkloadExecutable' --root '$GuestDataRoot' --oracle '$GuestDataRoot\$RunId-oracle.json' --scenario basic --count 10000 --run-id '$RunId'"
            $invokeArgs = @{ VmName = $definition.Name; Command = $guestCommand; ConfigPath = $ConfigPath }
            if ($null -ne $Credential) { $invokeArgs.Credential = $Credential }
            $guestOutput = @(& (Join-Path $PSScriptRoot 'Invoke-TestLabCommand.ps1') @invokeArgs 2>&1)
            if ($LASTEXITCODE -ne 0) { throw "Guest workload failed for $($definition.Name)." }
            $guestOutput | Out-File -LiteralPath (Join-Path $artifactDirectory "$($definition.Name)-workload.log") -Encoding UTF8
            Add-Stage $definition.Name 'PASSED' 'The real guest workload command completed; oracle/artifact copy is still required for acceptance.'
        }
    }

    if ($ExplorerEvidencePath -and (Test-Path -LiteralPath $ExplorerEvidencePath -PathType Leaf)) { Add-Stage 'explorer-correlation' 'REQUIRES_REVIEW' 'An evidence file was supplied; correlation must be checked for false Exact=0 and Unknown/Create correctness.' $ExplorerEvidencePath }
    else { Add-Stage 'explorer-correlation' 'NOT_EXECUTED' 'No independent human-assisted Explorer evidence was supplied.' }
    $manifest.Status = if (@($manifest.Stages | Where-Object Status -eq 'NOT_EXECUTED').Count -eq 0) { 'COMPLETED_NEEDS_REVIEW' } else { 'PARTIAL' }
    $manifest.CompletedUtc = [DateTimeOffset]::UtcNow
    Write-TestLabJson -Path $manifestPath -Value $manifest
    Write-Output ($manifest | ConvertTo-Json -Depth 12)
    exit 0
}
catch {
    $manifest.Status = 'FAILED'
    $manifest.Error = $_.Exception.Message
    $manifest.CompletedUtc = [DateTimeOffset]::UtcNow
    Write-TestLabJson -Path $manifestPath -Value $manifest
    Write-Error $_.Exception.Message
    exit 1
}
