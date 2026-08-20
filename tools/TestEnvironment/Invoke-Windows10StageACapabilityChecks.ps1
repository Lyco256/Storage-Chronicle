[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$RunId = ([guid]::NewGuid().ToString('N')),
    [string]$OutputDirectory,
    [pscredential]$Credential,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'TestLab.Common.ps1')

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\acceptance\testlab'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $artifactRoot "windows10-stage-a-capabilities\$RunId" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$manifestPath = Join-Path $OutputDirectory 'capability-execution.json'
$manifest = [ordered]@{
    Schema = 'StorageChronicle.Windows10StageACapabilityExecution.v1'
    RunId = $RunId
    TargetOs = 'Windows10-22H2'
    TargetKind = 'VirtualBoxVm'
    VmName = 'SC-Test-W10-VBox'
    ExecutionMode = 'VM'
    Apply = [bool]$Apply
    Status = 'NOT_EXECUTED'
    AcceptanceEligible = $false
    OutputDirectory = $OutputDirectory
    CloudFilesCheckPath = $null
    NoDriverCheckPath = $null
    Failure = $null
    GeneratedUtc = [DateTimeOffset]::UtcNow
}
$exitCode = 2

try {
    $null = Assert-PathUnderRoot -Root $artifactRoot -Path $OutputDirectory
    $config = Get-TestLabConfig -ConfigPath $ConfigPath
    $null = Assert-TestLabRoot -Root $config.Root
    if ([string]::IsNullOrWhiteSpace([string]$config.Windows10Iso)) { throw 'Windows 10 22H2 ISO is required for Stage A capability checks.' }
    Assert-ExistingIso -Path $config.Windows10Iso -Label 'Windows 10 22H2 ISO'
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

    if (-not $Apply) {
        $manifest.Failure = 'Pass -Apply only after the approved SC-Test-W10 VM is running; no guest command was executed.'
    } else {
        $null = Assert-VirtualBoxHostPrerequisites -Root $config.Root -Windows10Iso $config.Windows10Iso
        $vm = Assert-ExactTestLabVm -Name 'SC-Test-W10-VBox'
        if ([string]$vm.State -ne 'running') { throw 'SC-Test-W10-VBox is not running; the capability check did not start or modify a VM.' }
        $guestRoot = "C:\StorageChronicleAcceptance\StageA\Capabilities\$RunId"
        $guestScript = Join-Path $guestRoot 'Test-Windows10StageACapability.ps1'
        $guestOutput = Join-Path $guestRoot 'results'
        $sourceScript = Join-Path $PSScriptRoot 'Test-Windows10StageACapability.ps1'
        if (-not (Test-Path -LiteralPath $sourceScript -PathType Leaf)) { throw "Guest capability script is missing: $sourceScript" }
        $guestCredential = Get-TestLabGuestCredential -Credential $Credential -CredentialReference ([string]$config.GuestCredentialReference)
        $setup = "New-Item -ItemType Directory -Force -Path '$($guestRoot.Replace("'", "''"))', '$($guestOutput.Replace("'", "''"))' | Out-Null"
        $setupResult = & (Join-Path $PSScriptRoot 'Invoke-TestLabCommand.ps1') -VmName 'SC-Test-W10-VBox' -Command $setup -Credential $guestCredential -ConfigPath $ConfigPath 2>&1
        if ($LASTEXITCODE -ne 0) { throw "VirtualBox guest setup failed: $($setupResult -join [Environment]::NewLine)" }
        & (Join-Path $PSScriptRoot 'Copy-TestArtifactsToVm.ps1') -VmName 'SC-Test-W10-VBox' -SourcePath $sourceScript -DestinationPath $guestScript -Credential $guestCredential -ConfigPath $ConfigPath 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The Windows 10 capability script transfer failed.' }
        $command = "& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File '$($guestScript.Replace("'", "''"))' -OutputDirectory '$($guestOutput.Replace("'", "''"))' -RunId '$RunId' -VmName 'SC-Test-W10-VBox'; exit `$LASTEXITCODE"
        $remoteOutput = & (Join-Path $PSScriptRoot 'Invoke-TestLabCommand.ps1') -VmName 'SC-Test-W10-VBox' -Command $command -Credential $guestCredential -ConfigPath $ConfigPath 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Windows 10 capability guest script failed: $($remoteOutput -join [Environment]::NewLine)" }

            foreach ($name in @('CloudFilesCapability', 'NoDriver')) {
                $guestCheck = Join-Path $guestOutput "$name-check.json"
                $guestEvidence = Join-Path $guestOutput "$name-evidence.json"
                $hostCheck = Join-Path $OutputDirectory "$name-check.json"
                $hostEvidence = Join-Path $OutputDirectory "$name-evidence.json"
                & (Join-Path $PSScriptRoot 'Copy-TestResultsFromVm.ps1') -VmName 'SC-Test-W10-VBox' -SourcePath $guestEvidence -DestinationPath $hostEvidence -Credential $guestCredential -ConfigPath $ConfigPath 2>&1 | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "Could not retrieve $name evidence." }
                & (Join-Path $PSScriptRoot 'Copy-TestResultsFromVm.ps1') -VmName 'SC-Test-W10-VBox' -SourcePath $guestCheck -DestinationPath $hostCheck -Credential $guestCredential -ConfigPath $ConfigPath 2>&1 | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "Could not retrieve $name check." }
                $check = Get-Content -Raw -Encoding UTF8 -LiteralPath $hostCheck | ConvertFrom-Json
                $check.GuestEvidencePath = [string]$check.EvidencePath
                $check.EvidencePath = [IO.Path]::GetFullPath($hostEvidence)
                $check | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $hostCheck -Encoding UTF8
                $manifest["$name`CheckPath"] = [IO.Path]::GetFullPath($hostCheck)
            }
            $manifest.Status = 'PASSED'
            $manifest.AcceptanceEligible = $true
            $exitCode = 0
        }
} catch {
    $manifest.Status = if ($Apply) { 'FAILED' } else { 'NOT_EXECUTED' }
    $manifest.AcceptanceEligible = $false
    $manifest.Failure = $_.Exception.Message
    $exitCode = if ($Apply) { 1 } else { 2 }
} finally {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $manifestPath) | Out-Null
    $manifest.GeneratedUtc = [DateTimeOffset]::UtcNow
    $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Write-Output ($manifest | ConvertTo-Json -Depth 20)
}

exit $exitCode
