[CmdletBinding()]
param(
    [string]$StageAManifestPath,
    [string]$PhysicalPreflightPath,
    [string]$PhysicalInstallerManifestPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repositoryRoot 'build/quality/AcceptanceContracts.ps1')
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot ('artifacts/acceptance/windows10/windows10-physical-acceptance-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$requiredStageAChecks = @(Get-RequiredWindows10StageAChecks)

$manifest = [ordered]@{
    Schema = 'StorageChronicle.Windows10PhysicalAcceptance.v1'
    TargetOs = 'Windows10-22H2'
    Status = 'NOT_EXECUTED'
    AcceptanceEligible = $false
    StageA = $null
    StageB = $null
    Failure = $null
    GeneratedUtc = [DateTimeOffset]::UtcNow
    OutputPath = $OutputPath
}

function Read-JsonArtifact {
    param(
        [string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Label was not supplied." }
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "$Label does not exist: $fullPath" }
    try { return [pscustomobject]@{ Path = $fullPath; Value = Get-Content -Raw -Encoding UTF8 -LiteralPath $fullPath | ConvertFrom-Json } }
    catch { throw "$Label is not valid JSON: $fullPath. $($_.Exception.Message)" }
}

function Assert-StageA {
    param([Parameter(Mandatory = $true)]$Artifact)

    $value = $Artifact.Value
    if ([string]$value.Schema -ne 'StorageChronicle.Windows10StageAAcceptance.v1' -or
        [string]$value.TargetKind -ne 'HyperVVm' -or
        [string]$value.VmName -ne 'SC-Test-W10' -or
        [string]$value.ExecutionMode -ne 'VM') { throw "Stage A has an unexpected target or schema: $($Artifact.Path)" }
    if ([string]$value.TargetOs -ne 'Windows10-22H2' -or [string]$value.Status -ne 'PASSED' -or -not [bool]$value.AcceptanceEligible) { throw "Stage A is not an eligible Windows 10 22H2 acceptance artifact: $($Artifact.Path)" }
    $checks = @($value.Checks)
    if ($checks.Count -ne $requiredStageAChecks.Count) { throw 'Stage A does not contain exactly the required check count.' }
    $checkNames = @($checks | ForEach-Object { [string]$_.Name })
    if (@($checkNames | Sort-Object -Unique).Count -ne $requiredStageAChecks.Count -or @($requiredStageAChecks | Where-Object { $checkNames -notcontains $_ }).Count -ne 0) { throw 'Stage A checks are missing, duplicated, or contain an unexpected name.' }
    foreach ($name in $requiredStageAChecks) {
        $matches = @($checks | Where-Object { [string]$_.Name -eq $name })
        if ($matches.Count -ne 1 -or [string]$matches[0].Status -ne 'PASSED') { throw "Stage A check is not exactly PASSED: $name" }
        if ([string]$matches[0].Origin -ne 'real') { throw "Stage A check does not declare real evidence: $name" }
        $evidence = @($matches[0].Evidence | ForEach-Object { [string]$_ })
        if ($evidence.Count -eq 0 -or @($evidence | Where-Object { [string]::IsNullOrWhiteSpace($_) -or -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -ne 0) { throw "Stage A check evidence is missing: $name" }
    }
}

function Assert-PhysicalPreflight {
    param([Parameter(Mandatory = $true)]$Artifact)

    $value = $Artifact.Value
    if ([string]$value.Schema -ne 'StorageChronicle.Windows10PhysicalPreflight.v1') { throw "Physical Windows 10 preflight has an unexpected schema: $($Artifact.Path)" }
    if (-not [bool]$value.Ready) { throw "Physical Windows 10 preflight is not ready: $($Artifact.Path)" }
    $failed = @($value.Checks | Where-Object { [string]$_.Status -ne 'PASS' })
    if ($failed.Count -ne 0) { throw "Physical Windows 10 preflight contains non-PASS checks: $($Artifact.Path)" }
    if ([string]$value.Environment.ProductName -notmatch 'Windows 10' -or
        ([string]$value.Environment.DisplayVersion -ne '22H2' -and [string]$value.Environment.Build -ne '19045') -or
        [string]$value.Environment.Architecture -ne 'x64') {
        throw "Physical Windows 10 preflight does not prove Windows 10 22H2 x64: $($Artifact.Path)"
    }
}

function Assert-PhysicalInstaller {
    param([Parameter(Mandatory = $true)]$Artifact)

    $value = $Artifact.Value
    $requiredCaseIds = @(Get-RequiredInstallerCaseIds)
    if ([string]$value.Schema -ne 'storage-chronicle.installer-acceptance.v1') { throw "Physical installer artifact has an unexpected schema: $($Artifact.Path)" }
    if ([string]$value.Status -ne 'PASSED' -or -not [bool]$value.AcceptanceEligible) { throw "Physical installer artifact is not eligible: $($Artifact.Path)" }
    if ([string]$value.TargetOs -ne 'Windows10-22H2' -or [string]$value.TargetKind -ne 'PhysicalMachine' -or [string]$value.ExecutionMode -ne 'Local') { throw "Physical installer artifact has the wrong target: $($Artifact.Path)" }
    if ($null -eq $value.PSObject.Properties['Summary'] -or [int]$value.Summary.Total -ne $requiredCaseIds.Count -or [int]$value.Summary.Passed -ne $requiredCaseIds.Count -or [int]$value.Summary.Failed -ne 0 -or [int]$value.Summary.NotExecuted -ne 0 -or @($value.Tests).Count -ne $requiredCaseIds.Count -or @($value.Tests | Where-Object { [string]$_.Status -ne 'PASSED' }).Count -ne 0) { throw "Physical installer artifact does not prove all required cases passed: $($Artifact.Path)" }
    $caseIds = @($value.Tests | ForEach-Object { [string]$_.CaseId })
    if (@($caseIds | Sort-Object -Unique).Count -ne $requiredCaseIds.Count -or @($requiredCaseIds | Where-Object { $caseIds -notcontains $_ }).Count -ne 0) { throw "Physical installer artifact does not contain the defined eleven case IDs: $($Artifact.Path)" }
}

try {
    $stageA = Read-JsonArtifact -Path $StageAManifestPath -Label 'Stage A manifest'
    $preflight = Read-JsonArtifact -Path $PhysicalPreflightPath -Label 'Physical Windows 10 preflight'
    $installer = Read-JsonArtifact -Path $PhysicalInstallerManifestPath -Label 'Physical installer manifest'

    Assert-StageA -Artifact $stageA
    Assert-PhysicalPreflight -Artifact $preflight
    Assert-PhysicalInstaller -Artifact $installer

    $manifest.StageA = [ordered]@{ ManifestPath = $stageA.Path; Schema = [string]$stageA.Value.Schema; Status = [string]$stageA.Value.Status; AcceptanceEligible = [bool]$stageA.Value.AcceptanceEligible }
    $manifest.StageB = [ordered]@{
        PreflightPath = $preflight.Path
        PreflightSchema = [string]$preflight.Value.Schema
        InstallerManifestPath = $installer.Path
        InstallerSchema = [string]$installer.Value.Schema
        TargetKind = [string]$installer.Value.TargetKind
        ExecutionMode = [string]$installer.Value.ExecutionMode
        InstallerSummary = $installer.Value.Summary
    }
    $manifest.Status = 'PASSED'
    $manifest.AcceptanceEligible = $true
    Write-Host "Windows 10 physical acceptance composition passed: $OutputPath" -ForegroundColor Green
    $exitCode = 0
}
catch {
    $manifest.Status = 'NOT_EXECUTED'
    $manifest.Failure = $_.Exception.Message
    Write-Host "Windows 10 physical acceptance composition is not eligible: $($_.Exception.Message)" -ForegroundColor Yellow
    $exitCode = 2
}

$manifest.GeneratedUtc = [DateTimeOffset]::UtcNow
$manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$manifest | ConvertTo-Json -Depth 20
exit $exitCode
