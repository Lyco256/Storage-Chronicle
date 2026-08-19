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
    TargetKind = 'HyperVVm'
    VmName = 'SC-Test-W10'
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
        Assert-HyperVMutationPrerequisites
        $vm = Assert-ExactTestLabVm -Name 'SC-Test-W10'
        if ([string]$vm.State -ne 'Running') { throw 'SC-Test-W10 is not running; the capability check did not start or modify a VM.' }
        $guestRoot = "C:\StorageChronicleAcceptance\StageA\Capabilities\$RunId"
        $guestScript = Join-Path $guestRoot 'Test-Windows10StageACapability.ps1'
        $guestOutput = Join-Path $guestRoot 'results'
        $sourceScript = Join-Path $PSScriptRoot 'Test-Windows10StageACapability.ps1'
        if (-not (Test-Path -LiteralPath $sourceScript -PathType Leaf)) { throw "Guest capability script is missing: $sourceScript" }
        $sessionParameters = @{ VMName = 'SC-Test-W10'; ErrorAction = 'Stop' }
        if ($null -ne $Credential) { $sessionParameters.Credential = $Credential }
        $session = New-PSSession @sessionParameters
        try {
            Invoke-Command -Session $session -ScriptBlock { param($Root, $Results) New-Item -ItemType Directory -Force -Path $Root, $Results | Out-Null } -ArgumentList $guestRoot, $guestOutput | Out-Null
            Copy-Item -LiteralPath $sourceScript -Destination $guestScript -ToSession $session -Force -ErrorAction Stop
            $remoteOutput = @(Invoke-Command -Session $session -ScriptBlock {
                    param($Script, $Results, $ExecutionId)
                    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $Script -OutputDirectory $Results -RunId $ExecutionId -VmName 'SC-Test-W10'
                    [pscustomobject]@{ ExitCode = [int]$LASTEXITCODE }
                } -ArgumentList $guestScript, $guestOutput, $RunId)
            $remoteResult = $remoteOutput | Where-Object { $null -ne $_.PSObject.Properties['ExitCode'] } | Select-Object -Last 1
            if ($null -eq $remoteResult) { throw 'The Windows 10 capability guest script did not return an exit code.' }

            foreach ($name in @('CloudFilesCapability', 'NoDriver')) {
                $guestCheck = Join-Path $guestOutput "$name-check.json"
                $guestEvidence = Join-Path $guestOutput "$name-evidence.json"
                $hostCheck = Join-Path $OutputDirectory "$name-check.json"
                $hostEvidence = Join-Path $OutputDirectory "$name-evidence.json"
                Copy-Item -FromSession $session -LiteralPath $guestEvidence -Destination $hostEvidence -Force -ErrorAction Stop
                Copy-Item -FromSession $session -LiteralPath $guestCheck -Destination $hostCheck -Force -ErrorAction Stop
                $check = Get-Content -Raw -Encoding UTF8 -LiteralPath $hostCheck | ConvertFrom-Json
                $check.GuestEvidencePath = [string]$check.EvidencePath
                $check.EvidencePath = [IO.Path]::GetFullPath($hostEvidence)
                $check | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $hostCheck -Encoding UTF8
                $manifest["$name`CheckPath"] = [IO.Path]::GetFullPath($hostCheck)
            }
            if ([int]$remoteResult.ExitCode -ne 0) { throw "Windows 10 capability checks were not all eligible; guest exit code $($remoteResult.ExitCode)." }
            $manifest.Status = 'PASSED'
            $manifest.AcceptanceEligible = $true
            $exitCode = 0
        } finally {
            if ($null -ne $session) { Remove-PSSession $session -ErrorAction SilentlyContinue }
        }
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
