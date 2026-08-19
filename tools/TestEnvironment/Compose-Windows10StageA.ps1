[CmdletBinding()]
param(
    [string]$TestLabManifestPath,
    [string]$PrivilegedManifestPath,
    [string]$InstallerManifestPath,
    [string]$CloudFilesCheckPath,
    [string]$NoDriverCheckPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repositoryRoot 'build/quality/AcceptanceContracts.ps1')
$requiredWindowsPrivilegedCapabilities = @(Get-RequiredWindowsPrivilegedCapabilities)
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot ('artifacts/acceptance/windows10/windows10-stage-a-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null

$manifest = [ordered]@{
    Schema = 'StorageChronicle.Windows10StageAAcceptance.v1'
    TargetOs = 'Windows10-22H2'
    TargetKind = 'HyperVVm'
    VmName = 'SC-Test-W10'
    ExecutionMode = 'VM'
    Status = 'NOT_EXECUTED'
    AcceptanceEligible = $false
    Checks = @()
    Evidence = [ordered]@{}
    Failure = $null
    GeneratedUtc = [DateTimeOffset]::UtcNow
    OutputPath = $OutputPath
}

function Read-JsonArtifact {
    param([string]$Path, [string]$Label)

    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Label was not supplied." }
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "$Label does not exist: $fullPath" }
    try {
        return [pscustomobject]@{
            Path = $fullPath
            Value = Get-Content -Raw -Encoding UTF8 -LiteralPath $fullPath | ConvertFrom-Json
        }
    } catch {
        throw "$Label is not valid JSON: $fullPath. $($_.Exception.Message)"
    }
}

function Add-Check {
    param([string]$Name, [string[]]$EvidencePaths, [string]$Origin = 'real')
    $manifest.Checks += [ordered]@{
        Name = $Name
        Status = 'PASSED'
        Evidence = @($EvidencePaths)
        Origin = $Origin
    }
}

function Assert-RealCheckArtifact {
    param([Parameter(Mandatory = $true)]$Artifact, [Parameter(Mandatory = $true)][string]$ExpectedName)

    $value = $Artifact.Value
    if ([string]$value.Schema -ne 'StorageChronicle.Windows10StageACheck.v1' -or
        [string]$value.CheckName -ne $ExpectedName -or
        [string]$value.TargetOs -ne 'Windows10-22H2' -or
        [string]$value.TargetKind -ne 'HyperVVm' -or
        [string]$value.VmName -ne 'SC-Test-W10' -or
        [string]$value.ExecutionMode -ne 'VM' -or
        [string]$value.Status -ne 'PASSED' -or
        -not [bool]$value.AcceptanceEligible -or
        [bool]$value.Diagnostic -or
        [string]$value.EvidenceOrigin -ne 'real') {
        throw "Stage A check is not an eligible real Windows 10 VM result: $ExpectedName"
    }
    $evidencePath = [string]$value.EvidencePath
    if ([string]::IsNullOrWhiteSpace($evidencePath) -or -not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
        throw "Stage A check evidence is missing: $ExpectedName"
    }
    return $evidencePath
}

function Assert-TestLab {
    param([Parameter(Mandatory = $true)]$Artifact)

    $value = $Artifact.Value
    if ([string]$value.Schema -ne 'StorageChronicle.WindowsTestLabExecution.v2' -or
        [string]$value.Target -ne 'Windows10' -or
        [string]$value.Status -ne 'COMPLETED_REAL_IO_ACCEPTANCE' -or
        [string]$value.ExecutionMode -ne 'TestLab' -or
        $null -eq $value.PSObject.Properties['Diagnostic'] -or
        [bool]$value.Diagnostic -or
        -not [bool]$value.AcceptanceEligible -or
        -not [bool]$value.AgentIntegrationExecuted -or
        -not [bool]$value.RealIoAcceptance) {
        throw 'Windows 10 TestLab evidence is not a completed real-I/O Agent acceptance.'
    }
    $agentStages = @($value.Stages | Where-Object { [string]$_.Name -eq 'AgentIntegration-Windows10' })
    if ($agentStages.Count -ne 1 -or [string]$agentStages[0].Status -ne 'PASSED') { throw 'Windows 10 TestLab Agent integration stage is not exactly PASSED.' }
    $realIoPath = [string]$agentStages[0].Evidence
    if ([string]::IsNullOrWhiteSpace($realIoPath) -or -not (Test-Path -LiteralPath $realIoPath -PathType Leaf)) { throw 'Windows 10 TestLab real-I/O evidence is missing.' }
    $realIo = Read-JsonArtifact -Path $realIoPath -Label 'Windows 10 real-I/O evidence'
    if ([string]$realIo.Value.Schema -ne 'StorageChronicle.WindowsTestLabRealIoEvidence.v1' -or [string]$realIo.Value.Status -ne 'PASSED' -or -not [bool]$realIo.Value.AcceptanceEligible) { throw 'Windows 10 TestLab real-I/O evidence is not eligible.' }
    return $realIoPath
}

function Assert-Privileged {
    param([Parameter(Mandatory = $true)]$Artifact)

    $value = $Artifact.Value
    if ([string]$value.Schema -ne 'StorageChronicle.WindowsPrivilegedAcceptance.v2' -or [string]$value.Configuration -ne 'Release' -or [string]$value.Status -ne 'PASSED' -or -not [bool]$value.AcceptanceEligible) { throw 'Windows 10 privileged evidence is not an eligible Release result.' }
    if ([string]$value.Environment.ProductName -notmatch 'Windows 10' -or
        ([string]$value.Environment.DisplayVersion -ne '22H2' -and [string]$value.Environment.Build -ne '19045') -or
        [string]$value.Environment.Architecture -ne 'x64') { throw 'Privileged evidence does not prove Windows 10 22H2 x64.' }
    $required = @($value.RequiredCapabilities)
    $tests = @($value.Tests)
    if ($required.Count -ne $requiredWindowsPrivilegedCapabilities.Count -or
        (@($required | Sort-Object -Unique).Count -ne $required.Count) -or
        @($requiredWindowsPrivilegedCapabilities | Where-Object { $required -notcontains $_ }).Count -ne 0 -or
        @($required | Where-Object { $requiredWindowsPrivilegedCapabilities -notcontains $_ }).Count -ne 0) {
        throw 'Privileged evidence does not declare the complete required capability contract.'
    }
    foreach ($name in $requiredWindowsPrivilegedCapabilities) {
        $matches = @($tests | Where-Object { [string]$_.Capability -eq [string]$name })
        if ($matches.Count -ne 1 -or [string]$matches[0].Status -ne 'PASSED') { throw "Windows 10 privileged capability is not exactly PASSED: $name" }
    }
    return $Artifact.Path
}

function Assert-Installer {
    param([Parameter(Mandatory = $true)]$Artifact)

    $value = $Artifact.Value
    if ([string]$value.Schema -ne 'StorageChronicle.HyperVInstallerAcceptance.v1' -or
        [string]$value.Target -ne 'Windows10' -or
        [string]$value.TargetOs -ne 'Windows10-22H2' -or
        [string]$value.Status -ne 'PASSED' -or
        -not [bool]$value.AcceptanceEligible) { throw 'Windows 10 Hyper-V installer evidence is not eligible.' }
    $genericPath = [string]$value.InstallerManifestPath
    $generic = Read-JsonArtifact -Path $genericPath -Label 'Windows 10 Hyper-V installer manifest'
    $genericValue = $generic.Value
    if ([string]$genericValue.Schema -ne 'storage-chronicle.installer-acceptance.v1' -or
        [string]$genericValue.Status -ne 'PASSED' -or
        -not [bool]$genericValue.AcceptanceEligible -or
        [string]$genericValue.TargetOs -ne 'Windows10-22H2' -or
        [string]$genericValue.TargetKind -ne 'HyperVVm' -or
        [string]$genericValue.ExecutionMode -ne 'VM' -or
        $null -eq $genericValue.Summary -or
        [int]$genericValue.Summary.Total -ne 11 -or
        [int]$genericValue.Summary.Passed -ne 11 -or
        [int]$genericValue.Summary.Failed -ne 0 -or
        [int]$genericValue.Summary.NotExecuted -ne 0 -or
        @($genericValue.Tests).Count -ne 11 -or
        @($genericValue.Tests | Where-Object { [string]$_.Status -ne 'PASSED' }).Count -ne 0) {
        throw 'Windows 10 Hyper-V installer manifest does not prove all eleven cases passed.'
    }
    $requiredCaseIds = @(Get-RequiredInstallerCaseIds)
    $caseIds = @($genericValue.Tests | ForEach-Object { [string]$_.CaseId })
    if (@($caseIds | Sort-Object -Unique).Count -ne $requiredCaseIds.Count -or @($requiredCaseIds | Where-Object { $caseIds -notcontains $_ }).Count -ne 0) { throw 'Windows 10 Hyper-V installer manifest does not contain the defined eleven case IDs.' }
    return $generic.Path
}

try {
    $testLab = Read-JsonArtifact -Path $TestLabManifestPath -Label 'Windows 10 TestLab manifest'
    $privileged = Read-JsonArtifact -Path $PrivilegedManifestPath -Label 'Windows 10 privileged manifest'
    $installer = Read-JsonArtifact -Path $InstallerManifestPath -Label 'Windows 10 Hyper-V installer manifest'
    $cloudFiles = Read-JsonArtifact -Path $CloudFilesCheckPath -Label 'Windows 10 Cloud Files check'
    $noDriver = Read-JsonArtifact -Path $NoDriverCheckPath -Label 'Windows 10 no-driver check'

    $realIoPath = Assert-TestLab -Artifact $testLab
    $privilegedPath = Assert-Privileged -Artifact $privileged
    $installerPath = Assert-Installer -Artifact $installer
    $cloudFilesEvidence = Assert-RealCheckArtifact -Artifact $cloudFiles -ExpectedName 'CloudFilesCapability'
    $noDriverEvidence = Assert-RealCheckArtifact -Artifact $noDriver -ExpectedName 'NoDriver'
    if ($null -eq $noDriver.Value.PSObject.Properties['DriverPresent'] -or [bool]$noDriver.Value.DriverPresent) { throw 'The no-driver check does not prove that no product driver is present.' }

    Add-Check 'Application' @($installerPath)
    Add-Check 'AvaloniaUI' @($installerPath)
    Add-Check 'Agent' @($realIoPath, $installerPath)
    Add-Check 'SessionAgent' @($privilegedPath, $installerPath)
    Add-Check 'Usn' @($privilegedPath)
    Add-Check 'Mft' @($privilegedPath)
    Add-Check 'Etw' @($privilegedPath)
    Add-Check 'ReadDirectoryChangesW' @($privilegedPath)
    Add-Check 'Clipboard' @($privilegedPath)
    Add-Check 'Smb' @($privilegedPath)
    Add-Check 'CloudFilesCapability' @($cloudFilesEvidence)
    Add-Check 'Reconciliation' @($realIoPath, $privilegedPath)
    Add-Check 'Installer' @($installerPath)
    Add-Check 'HistoryRetention' @($installerPath)
    Add-Check 'NoDriver' @($noDriverEvidence)
    $manifest.Evidence = [ordered]@{
        TestLab = $testLab.Path
        Privileged = $privileged.Path
        Installer = $installer.Path
        GenericInstaller = $installerPath
        CloudFiles = $cloudFiles.Path
        NoDriver = $noDriver.Path
    }
    $manifest.Status = 'PASSED'
    $manifest.AcceptanceEligible = $true
    Write-Host "Windows 10 Stage A composition passed: $OutputPath" -ForegroundColor Green
    $exitCode = 0
} catch {
    $manifest.Status = 'NOT_EXECUTED'
    $manifest.Failure = $_.Exception.Message
    Write-Host "Windows 10 Stage A composition is not eligible: $($_.Exception.Message)" -ForegroundColor Yellow
    $exitCode = 2
}

$manifest.GeneratedUtc = [DateTimeOffset]::UtcNow
$manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$manifest | ConvertTo-Json -Depth 20
exit $exitCode
