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

function Assert-GroupEvidence {
    param([Parameter(Mandatory = $true)][string]$Name, [Parameter(Mandatory = $true)]$Value)

    $schema = if ($null -ne $Value.PSObject.Properties['Schema']) { [string]$Value.Schema } else { '' }
    switch ($Name) {
        'TestLabAndRealIo' {
            if ($schema -ne 'StorageChronicle.WindowsTestLabExecution.v2') { throw 'TestLab evidence has an unexpected schema.' }
            if (@($Value.Stages).Count -eq 0) { throw 'TestLab evidence has no execution stages.' }
            if (-not [bool]$Value.AgentIntegrationExecuted -or -not [bool]$Value.RealIoAcceptance) { throw 'TestLab evidence does not prove the real Agent and Oracle comparison path.' }
            $agentStages = @($Value.Stages | Where-Object { [string]$_.Name -like 'AgentIntegration-*' })
            if ($agentStages.Count -eq 0) { throw 'TestLab evidence has no Agent integration stages.' }
            foreach ($stage in $agentStages) {
                if ([string]$stage.Status -ne 'PASSED') { throw "Agent integration stage is not PASSED: $($stage.Name)" }
                $evidencePath = [string]$stage.Evidence
                if ([string]::IsNullOrWhiteSpace($evidencePath) -or -not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) { throw "Agent integration evidence is missing: $($stage.Name)" }
                $realIo = Get-Content -Raw -Encoding UTF8 -LiteralPath $evidencePath | ConvertFrom-Json
                if ([string]$realIo.Schema -ne 'StorageChronicle.WindowsTestLabRealIoEvidence.v1' -or [string]$realIo.Status -ne 'PASSED' -or -not [bool]$realIo.AcceptanceEligible) { throw "Real-I/O evidence is not eligible: $evidencePath" }
            }
        }
        'ConfirmedReconciliation' {
            if ($schema -ne 'StorageChronicle.ConfirmedReconciliationAcceptance.v1') { throw 'Confirmed reconciliation evidence has an unexpected schema.' }
            foreach ($field in @('RunId', 'VolumeId', 'FileSystem', 'SourceEventCount', 'CanonicalEventCount', 'FinalStateCount', 'LightweightEntryCount', 'MftEntryCount', 'CandidateCount', 'DetailedMetadataQueryCount', 'DetailedQueryCandidateRatio', 'PrivilegeEnableSuccessCount', 'PrivilegeEnableFailureCount', 'AclFallbackCount', 'BackgroundModeEnabled', 'IoHintAttempts', 'IoHintSuccesses', 'IoHintFailures', 'ElapsedMilliseconds', 'Status')) {
                if ($null -eq $Value.PSObject.Properties[$field]) { throw "Confirmed reconciliation evidence is missing $field." }
            }
            if ([int64]$Value.MftEntryCount -ne [int64]$Value.LightweightEntryCount) { throw 'Confirmed reconciliation MFT and lightweight entry counts disagree.' }
            if ([string]$Value.Status -ne 'PASSED') { throw "Confirmed reconciliation evidence status is not PASSED: $($Value.Status)" }
            if ([int64]$Value.CandidateCount -eq 0 -and [int64]$Value.DetailedMetadataQueryCount -ne 0) { throw 'Confirmed reconciliation performed detailed metadata queries without candidates.' }
            $expectedRatio = if ([int64]$Value.CandidateCount -eq 0) { 0d } else { [double]$Value.DetailedMetadataQueryCount / [double]$Value.CandidateCount }
            if ([math]::Abs([double]$Value.DetailedQueryCandidateRatio - $expectedRatio) -gt 0.000001) { throw 'Confirmed reconciliation detailed-query ratio does not match its counters.' }
            if ([double]$Value.ElapsedMilliseconds -lt 0) { throw 'Confirmed reconciliation elapsed time is negative.' }
        }
        'WindowsPrivileged' {
            if ($schema -ne 'StorageChronicle.WindowsPrivilegedAcceptance.v2') { throw 'Windows privileged evidence has an unexpected schema.' }
            $required = @($Value.RequiredCapabilities)
            $tests = @($Value.Tests)
            if ($required.Count -lt 1) { throw 'Windows privileged evidence has no required capabilities.' }
            foreach ($capability in $required) {
                $matches = @($tests | Where-Object { [string]$_.Capability -eq [string]$capability })
                if ($matches.Count -ne 1 -or [string]$matches[0].Status -ne 'PASSED') { throw "Windows privileged capability is not exactly PASSED: $capability" }
            }
        }
        'Windows10_22H2' {
            if ($schema -ne 'StorageChronicle.Windows10PhysicalAcceptance.v1') { throw 'Windows 10 evidence has an unexpected schema.' }
            if ([string]$Value.TargetOs -ne 'Windows10-22H2') { throw 'Windows 10 evidence does not identify Windows10-22H2.' }
        }
        'IdleResource' {
            if ($schema -ne 'StorageChronicle.ResourceBudgetAcceptanceEvidence.v1') { throw 'Resource evidence has an unexpected schema.' }
            if ($null -eq $Value.PSObject.Properties['EvidenceChecks']) { throw 'Resource evidence has no supervised gate checks.' }
        }
        'MftPerformance' {
            if ($schema -ne 'StorageChronicle.FullBenchmarkMatrixEvidence.v1') { throw 'MFT performance evidence has an unexpected schema.' }
            if (-not [bool]$Value.IncludeMft -or $null -eq $Value.MftEvidence) { throw 'MFT performance evidence is missing the connected MFT correctness artifact.' }
        }
        'PhysicalInstaller' {
            if ($schema -ne 'storage-chronicle.installer-acceptance.v1') { throw 'Installer evidence has an unexpected schema.' }
            if ($null -eq $Value.PSObject.Properties['Summary'] -or [int]$Value.Summary.Total -ne 11 -or [int]$Value.Summary.Passed -ne 11) { throw 'Installer evidence does not contain all eleven passed cases.' }
            if ([string]$Value.TargetKind -ne 'PhysicalMachine' -or [string]$Value.ExecutionMode -ne 'Local') { throw 'Installer evidence is not from the required physical-machine acceptance path.' }
        }
        'AgentExplorerCorrelation' {
            if ($schema -ne 'StorageChronicle.AgentExplorerCorrelationEvidence.v1') { throw 'Agent/Explorer correlation evidence has an unexpected schema.' }
            if ([string]$Value.LiveMachineMeasurement -ne 'PASSED') { throw 'Agent/Explorer evidence is not a live machine measurement.' }
            if ($null -eq $Value.PSObject.Properties['FalseExactCount'] -or [int]$Value.FalseExactCount -ne 0) { throw 'Agent/Explorer evidence does not prove false Exact attribution is zero.' }
        }
        'BranchIntegration' {
            if ($schema -ne 'StorageChronicle.BranchIntegrationEvidence.v1') { throw 'Branch integration evidence has an unexpected schema.' }
            foreach ($field in @('CurrentBranch', 'HeadSha', 'ExpectedAcceptedSha', 'ExpectedAcceptedShaMatch', 'RemoteDevenv', 'RemoteMain', 'WorktreeClean', 'DevenvSha', 'MainSha', 'MainContainsDevenv')) {
                if ($null -eq $Value.PSObject.Properties[$field]) { throw "Branch integration evidence is missing $field." }
            }
            if ([string]$Value.CurrentBranch -ne 'main') { throw 'Branch integration evidence was not captured on main.' }
            if (-not [bool]$Value.RemoteDevenv -or -not [bool]$Value.RemoteMain) { throw 'Both remote devenv and main branches are required.' }
            if (-not [bool]$Value.WorktreeClean) { throw 'The final integration worktree is not clean.' }
            if (-not [bool]$Value.ExpectedAcceptedShaMatch) { throw 'HEAD does not match the accepted integration SHA.' }
            if (-not [bool]$Value.MainContainsDevenv) { throw 'main does not contain devenv.' }
            if ([string]$Value.HeadSha -ne [string]$Value.MainSha) { throw 'The recorded HEAD and origin/main SHA differ.' }
        }
        default { throw "Unknown final acceptance group: $Name" }
    }
}

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
        Assert-GroupEvidence -Name $group.Name -Value $value
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
