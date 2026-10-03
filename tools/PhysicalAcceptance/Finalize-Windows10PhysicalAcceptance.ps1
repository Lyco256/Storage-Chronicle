[CmdletBinding()]
param(
    [string]$Windows10PrivilegedManifestPath,
    [string]$PhysicalPreflightPath,
    [string]$PhysicalInstallerManifestPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$contractCandidates = @(
    (Join-Path $repositoryRoot 'build/quality/AcceptanceContracts.ps1'),
    (Join-Path $PSScriptRoot 'AcceptanceContracts.ps1')
)
$contractPath = @($contractCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
if ($contractPath.Count -ne 1) { throw 'AcceptanceContracts.ps1 is missing from the repository or manual Windows 10 bundle.' }
. $contractPath[0]
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot ('artifacts/acceptance/windows10/windows10-physical-acceptance-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$requiredCapabilities = @(Get-RequiredWindowsPrivilegedCapabilities)

$manifest = [ordered]@{
    Schema = 'StorageChronicle.Windows10PhysicalAcceptance.v1'
    TargetOs = 'Windows10-22H2'
    Status = 'NOT_EXECUTED'
    AcceptanceEligible = $false
    PhysicalCapabilities = $null
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

function Assert-PhysicalCapabilities {
    param([Parameter(Mandatory = $true)]$Artifact)

    $value = $Artifact.Value
    if ([string]$value.Schema -ne 'StorageChronicle.WindowsPrivilegedAcceptance.v2') { throw "Windows 10 privileged evidence has an unexpected schema: $($Artifact.Path)" }
    if ([string]$value.Status -ne 'PASSED' -or -not [bool]$value.AcceptanceEligible -or [string]$value.Configuration -ne 'Release') { throw "Windows 10 privileged evidence is not eligible: $($Artifact.Path)" }
    if ([string]$value.Environment.ProductName -notmatch 'Windows 10' -or
        ([string]$value.Environment.DisplayVersion -ne '22H2' -and [string]$value.Environment.Build -ne '19045') -or
        [string]$value.Environment.Architecture -ne 'x64' -or -not [bool]$value.Environment.IsAdministrator) { throw "Privileged evidence does not prove an elevated Windows 10 22H2 x64 host: $($Artifact.Path)" }
    if ([string]::IsNullOrWhiteSpace([string]$value.Environment.ComputerName)) { throw 'Windows 10 privileged evidence is not bound to a physical computer name.' }
    $declared = @($value.RequiredCapabilities | ForEach-Object { [string]$_ })
    $tests = @($value.Tests)
    if ($declared.Count -ne $requiredCapabilities.Count -or @($declared | Sort-Object -Unique).Count -ne $declared.Count -or @($requiredCapabilities | Where-Object { $declared -notcontains $_ }).Count -ne 0) { throw 'Windows 10 privileged evidence does not declare the complete capability contract.' }
    if (@($tests).Count -ne $requiredCapabilities.Count) { throw 'Windows 10 privileged evidence does not contain exactly the required capability results.' }
    foreach ($capability in $requiredCapabilities) {
        $matches = @($tests | Where-Object { [string]$_.Capability -eq $capability })
        if ($matches.Count -ne 1 -or [string]$matches[0].Status -ne 'PASSED') { throw "Windows 10 privileged capability is not exactly PASSED: $capability" }
    }
    foreach ($artifactName in @('Environment', 'Capabilities', 'Oracle', 'SourceEventSummary', 'CanonicalSummary', 'FinalStateSummary', 'ReconciliationSummary', 'ConfirmedReconciliation', 'ServiceSummary', 'Errors', 'Result')) {
        $property = $value.Artifacts.PSObject.Properties[$artifactName]
        if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value) -or -not (Test-Path -LiteralPath ([string]$property.Value) -PathType Leaf)) { throw "Windows 10 privileged artifact is missing: $artifactName" }
    }
    $capabilitiesArtifact = Get-Content -Raw -Encoding UTF8 -LiteralPath ([string]$value.Artifacts.Capabilities) | ConvertFrom-Json
    if ([string]$capabilitiesArtifact.Schema -ne 'StorageChronicle.WindowsPrivilegedCapabilities.v1' -or @($capabilitiesArtifact.Required).Count -ne $requiredCapabilities.Count -or @($capabilitiesArtifact.Tests | Where-Object { [string]$_.Status -ne 'PASSED' }).Count -ne 0) { throw 'Windows 10 privileged capability artifact is incomplete.' }
    $errors = Get-Content -Raw -Encoding UTF8 -LiteralPath ([string]$value.Artifacts.Errors) | ConvertFrom-Json
    if ([string]$errors.Schema -ne 'StorageChronicle.WindowsPrivilegedErrors.v1' -or @($errors.Errors).Count -ne 0) { throw 'Windows 10 privileged evidence contains errors.' }
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
    if ([string]::IsNullOrWhiteSpace([string]$value.Environment.ComputerName)) { throw 'Physical Windows 10 preflight is not bound to a computer name.' }
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
    if ([string]::IsNullOrWhiteSpace([string]$value.Environment.ComputerName)) { throw 'Physical installer evidence is not bound to a computer name.' }
}

try {
    $privileged = Read-JsonArtifact -Path $Windows10PrivilegedManifestPath -Label 'Windows 10 privileged acceptance'
    $preflight = Read-JsonArtifact -Path $PhysicalPreflightPath -Label 'Physical Windows 10 preflight'
    $installer = Read-JsonArtifact -Path $PhysicalInstallerManifestPath -Label 'Physical installer manifest'

    Assert-PhysicalCapabilities -Artifact $privileged
    Assert-PhysicalPreflight -Artifact $preflight
    Assert-PhysicalInstaller -Artifact $installer
    $privilegedComputer = [string]$privileged.Value.Environment.ComputerName
    $preflightComputer = [string]$preflight.Value.Environment.ComputerName
    $installerComputer = [string]$installer.Value.Environment.ComputerName
    if (-not $privilegedComputer.Equals($preflightComputer, [StringComparison]::OrdinalIgnoreCase) -or
        -not $privilegedComputer.Equals($installerComputer, [StringComparison]::OrdinalIgnoreCase)) { throw 'Windows 10 privileged, preflight, and installer evidence are not from the same physical computer.' }

    $manifest.PhysicalCapabilities = [ordered]@{ ManifestPath = $privileged.Path; Schema = [string]$privileged.Value.Schema; Status = [string]$privileged.Value.Status; AcceptanceEligible = [bool]$privileged.Value.AcceptanceEligible; CapabilityCount = $requiredCapabilities.Count }
    $manifest.StageB = [ordered]@{
        PrivilegedManifestPath = $privileged.Path
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
Write-NewJsonArtifact -Path $OutputPath -Value $manifest -Depth 20
$manifest | ConvertTo-Json -Depth 20
exit $exitCode
