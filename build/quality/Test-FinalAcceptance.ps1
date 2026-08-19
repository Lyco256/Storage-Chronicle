[CmdletBinding()]
param(
    [string]$TestLabManifest,
    [string]$ReconciliationManifest,
    [string]$PrivilegedManifest,
    [string]$Windows10Manifest,
    [string]$ResourceEvidence,
    [string]$BenchmarkManifest,
    [string]$InstallerManifest,
    [string]$CorrelationManifest,
    [string]$BranchManifest,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $root ('artifacts/acceptance/final/final-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json') }
$artifactDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

$groups = @(
    [ordered]@{ Name = 'TestLabAndRealIo'; Path = $TestLabManifest },
    [ordered]@{ Name = 'ConfirmedReconciliation'; Path = $ReconciliationManifest },
    [ordered]@{ Name = 'WindowsPrivileged'; Path = $PrivilegedManifest },
    [ordered]@{ Name = 'Windows10_22H2'; Path = $Windows10Manifest },
    [ordered]@{ Name = 'IdleResource'; Path = $ResourceEvidence },
    [ordered]@{ Name = 'MftPerformance'; Path = $BenchmarkManifest },
    [ordered]@{ Name = 'PhysicalInstaller'; Path = $InstallerManifest },
    [ordered]@{ Name = 'AgentExplorerCorrelation'; Path = $CorrelationManifest },
    [ordered]@{ Name = 'BranchIntegration'; Path = $BranchManifest }
)

$results = [System.Collections.Generic.List[object]]::new()
$blocking = [System.Collections.Generic.List[string]]::new()
foreach ($group in $groups) {
    $path = [string]$group.Path
    $result = [ordered]@{ Name = $group.Name; Path = $path; Status = 'NOT_EXECUTED'; AcceptanceEligible = $false; Reason = $null }
    if ([string]::IsNullOrWhiteSpace($path)) {
        $result.Reason = 'No acceptance artifact was supplied.'
        $blocking.Add("$($group.Name):missing")
        $results.Add([pscustomobject]$result)
        continue
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $result.Reason = "Artifact does not exist: $path"
        $blocking.Add("$($group.Name):missing")
        $results.Add([pscustomobject]$result)
        continue
    }
    try {
        $value = Get-Content -Raw -Encoding UTF8 -LiteralPath $path | ConvertFrom-Json
        $eligibleProperty = $value.PSObject.Properties['AcceptanceEligible']
        $eligible = $null -ne $eligibleProperty -and [bool]$eligibleProperty.Value
        $statusProperty = $value.PSObject.Properties['Status']
        $executionStatusProperty = $value.PSObject.Properties['ExecutionStatus']
        $status = if ($null -ne $statusProperty) { [string]$statusProperty.Value } elseif ($null -ne $executionStatusProperty) { [string]$executionStatusProperty.Value } else { 'UNKNOWN' }
        if (-not $eligible) {
            $result.Reason = 'Artifact is present but AcceptanceEligible is not true.'
            $blocking.Add("$($group.Name):ineligible")
        }
        elseif ($status -match '(?i)NOT[_-]?EXECUTED|FAILED|PENDING|DIAGNOSTIC|PARTIAL') {
            $result.Reason = "Artifact status is not an acceptance status: $status"
            $blocking.Add("$($group.Name):$status")
        }
        else {
            $result.Status = 'PASSED'
            $result.AcceptanceEligible = $true
            $result.Reason = "Artifact status=$status"
        }
    }
    catch {
        $result.Status = 'FAILED'
        $result.Reason = "Could not parse acceptance artifact: $($_.Exception.Message)"
        $blocking.Add("$($group.Name):invalid")
    }
    $results.Add([pscustomobject]$result)
}

$evidence = [ordered]@{
    Schema = 'StorageChronicle.FinalAcceptanceEvidence.v1'
    ExecutionStatus = if ($blocking.Count -eq 0) { 'completed' } else { 'blocked' }
    AcceptanceEligible = $blocking.Count -eq 0
    Groups = @($results)
    BlockingGroups = @($blocking)
    GeneratedUtc = [DateTimeOffset]::UtcNow
    OutputPath = $OutputPath
}
$evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Output ($evidence | ConvertTo-Json -Depth 12)
if ($blocking.Count -ne 0) { exit 2 }
exit 0
