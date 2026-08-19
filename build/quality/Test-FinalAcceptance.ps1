[CmdletBinding()]
param(
    [string]$TestLabManifest,
    [string]$ReconciliationManifest,
    [string]$PrivilegedManifest,
    [string]$Windows10Manifest,
    [string]$ResourceEvidence,
    [string]$BenchmarkManifest,
    [string]$InstallerManifest,
    [string]$Windows11HyperVInstallerManifest,
    [string]$CorrelationManifest,
    [string]$BranchManifest,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $PSScriptRoot 'AcceptanceContracts.ps1')
$requiredWindowsPrivilegedCapabilities = @(Get-RequiredWindowsPrivilegedCapabilities)
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $root ('artifacts/acceptance/final/final-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json') }
$artifactDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

$requiredWindows10StageAChecks = @(Get-RequiredWindows10StageAChecks)
$requiredInstallerCaseIds = @(Get-RequiredInstallerCaseIds)

function Read-ReferencedJson {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Label)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label is missing: $Path" }
    try { return Get-Content -Raw -Encoding UTF8 -LiteralPath $Path | ConvertFrom-Json }
    catch { throw "$Label is not valid JSON: $Path. $($_.Exception.Message)" }
}

function Assert-InstallerCaseRows {
    param([Parameter(Mandatory = $true)]$Value, [Parameter(Mandatory = $true)][string]$Label)

    if ($null -eq $Value.PSObject.Properties['Summary'] -or
        [int]$Value.Summary.Total -ne $requiredInstallerCaseIds.Count -or
        [int]$Value.Summary.Passed -ne $requiredInstallerCaseIds.Count -or
        [int]$Value.Summary.Failed -ne 0 -or
        [int]$Value.Summary.NotExecuted -ne 0 -or
        @($Value.Tests).Count -ne $requiredInstallerCaseIds.Count -or
        @($Value.Tests | Where-Object { [string]$_.Status -ne 'PASSED' }).Count -ne 0) {
        throw "$Label does not prove all required installer cases passed."
    }
    $caseIds = @($Value.Tests | ForEach-Object { [string]$_.CaseId })
    if (@($caseIds | Sort-Object -Unique).Count -ne $requiredInstallerCaseIds.Count -or
        @($requiredInstallerCaseIds | Where-Object { $caseIds -notcontains $_ }).Count -ne 0) {
        throw "$Label does not contain the defined installer case IDs."
    }
}

function Assert-Windows11HyperVInstallerPrerequisite {
    param([Parameter(Mandatory = $true)][string]$Path)

    $wrapper = Read-ReferencedJson -Path $Path -Label 'Windows 11 Hyper-V installer acceptance manifest'
    if ([string]$wrapper.Schema -ne 'StorageChronicle.HyperVInstallerAcceptance.v1' -or
        [string]$wrapper.Target -ne 'Windows11' -or
        [string]$wrapper.TargetOs -ne 'Windows11' -or
        [string]$wrapper.Status -ne 'PASSED' -or
        -not [bool]$wrapper.AcceptanceEligible) {
        throw 'Windows 11 Hyper-V installer acceptance is not an eligible passed artifact.'
    }
    $generic = Read-ReferencedJson -Path ([string]$wrapper.InstallerManifestPath) -Label 'Windows 11 Hyper-V generic installer manifest'
    if ([string]$generic.Schema -ne 'storage-chronicle.installer-acceptance.v1' -or
        [string]$generic.TargetOs -ne 'Windows11' -or
        [string]$generic.TargetKind -ne 'HyperVVm' -or
        [string]$generic.ExecutionMode -ne 'VM' -or
        [string]$generic.Status -ne 'PASSED' -or
        -not [bool]$generic.AcceptanceEligible) {
        throw 'Windows 11 Hyper-V generic installer evidence is not eligible.'
    }
    Assert-InstallerCaseRows -Value $generic -Label 'Windows 11 Hyper-V generic installer evidence'
}

function Assert-GroupEvidence {
    param([Parameter(Mandatory = $true)][string]$Name, [Parameter(Mandatory = $true)]$Value)

    $schema = if ($null -ne $Value.PSObject.Properties['Schema']) { [string]$Value.Schema } else { '' }
    if ($Name -ne 'BranchIntegration' -and ($null -eq $Value.PSObject.Properties['AcceptanceEligible'] -or -not [bool]$Value.AcceptanceEligible)) { throw "$Name evidence is not marked AcceptanceEligible=true." }
    switch ($Name) {
        'TestLabAndRealIo' {
            if ($schema -ne 'StorageChronicle.WindowsTestLabExecution.v2' -or
                [string]$Value.Status -ne 'COMPLETED_REAL_IO_ACCEPTANCE' -or
                [string]$Value.ExecutionMode -ne 'TestLab' -or
                $null -eq $Value.PSObject.Properties['Diagnostic'] -or
                [bool]$Value.Diagnostic) { throw 'TestLab evidence is not an eligible non-diagnostic TestLab execution.' }
            if ([string]$Value.Target -notin @('Windows11', 'Both')) { throw 'TestLab evidence does not include the required Windows 11 target.' }
            if (@($Value.Stages).Count -eq 0) { throw 'TestLab evidence has no execution stages.' }
            if (-not [bool]$Value.AgentIntegrationExecuted -or -not [bool]$Value.RealIoAcceptance) { throw 'TestLab evidence does not prove the real Agent and Oracle comparison path.' }
            $agentStages = @($Value.Stages | Where-Object { [string]$_.Name -like 'AgentIntegration-*' })
            if ($agentStages.Count -eq 0) { throw 'TestLab evidence has no Agent integration stages.' }
            $windows11AgentStages = @($agentStages | Where-Object { [string]$_.Name -eq 'AgentIntegration-Windows11' })
            if ($windows11AgentStages.Count -ne 1) { throw 'TestLab evidence does not contain exactly one Windows 11 Agent integration stage.' }
            foreach ($stage in $agentStages) {
                if ([string]$stage.Status -ne 'PASSED') { throw "Agent integration stage is not PASSED: $($stage.Name)" }
                $evidencePath = [string]$stage.Evidence
                if ([string]::IsNullOrWhiteSpace($evidencePath) -or -not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) { throw "Agent integration evidence is missing: $($stage.Name)" }
                $realIo = Get-Content -Raw -Encoding UTF8 -LiteralPath $evidencePath | ConvertFrom-Json
                if ([string]$realIo.Schema -ne 'StorageChronicle.WindowsTestLabRealIoEvidence.v1' -or [string]$realIo.Status -ne 'PASSED' -or -not [bool]$realIo.AcceptanceEligible) { throw "Real-I/O evidence is not eligible: $evidencePath" }
                foreach ($field in @('OracleOperationCount', 'SourceEventCount', 'CanonicalEventCount', 'FinalStateCount', 'Checks', 'FailureReasons', 'HistoryPath')) { if ($null -eq $realIo.PSObject.Properties[$field]) { throw "Real-I/O evidence is missing ${field}: $evidencePath" } }
                if ([int64]$realIo.OracleOperationCount -le 0 -or [int64]$realIo.SourceEventCount -le 0 -or [int64]$realIo.CanonicalEventCount -le 0 -or [int64]$realIo.FinalStateCount -le 0 -or @($realIo.FailureReasons).Count -ne 0 -or @($realIo.Checks).Count -eq 0 -or @($realIo.Checks | Where-Object { [string]$_.Status -ne 'PASSED' }).Count -ne 0 -or -not (Test-Path -LiteralPath ([string]$realIo.HistoryPath) -PathType Container)) { throw "Real-I/O evidence contains incomplete durable counts, failed checks, or a missing history path: $evidencePath" }
            }
        }
        'ConfirmedReconciliation' {
            if ($schema -ne 'StorageChronicle.ConfirmedReconciliationAcceptance.v1') { throw 'Confirmed reconciliation evidence has an unexpected schema.' }
            foreach ($field in @('RunId', 'VolumeId', 'FileSystem', 'SourceEventCount', 'CanonicalEventCount', 'FinalStateCount', 'LightweightEntryCount', 'MftEntryCount', 'CandidateCount', 'DetailedMetadataQueryCount', 'DetailedQueryCandidateRatio', 'PrivilegeEnableSuccessCount', 'PrivilegeEnableFailureCount', 'AclFallbackCount', 'BackgroundModeEnabled', 'IoHintAttempts', 'IoHintSuccesses', 'IoHintFailures', 'StartJournalId', 'StartJournalNextUsn', 'CompletionJournalId', 'CompletionJournalNextUsn', 'ScanCompletedUtc', 'LiveEventCount', 'LiveEventsDeduplicated', 'LiveEventsAccepted', 'ElapsedMilliseconds', 'Status')) {
                if ($null -eq $Value.PSObject.Properties[$field]) { throw "Confirmed reconciliation evidence is missing $field." }
            }
            if ([int64]$Value.MftEntryCount -ne [int64]$Value.LightweightEntryCount) { throw 'Confirmed reconciliation MFT and lightweight entry counts disagree.' }
            if ([string]$Value.Status -ne 'PASSED') { throw "Confirmed reconciliation evidence status is not PASSED: $($Value.Status)" }
            if ([string]$Value.FileSystem -ne 'NTFS') { throw 'Confirmed reconciliation evidence must come from the required NTFS acceptance path.' }
            if ([string]::IsNullOrWhiteSpace([string]$Value.StartJournalId) -or [string]::IsNullOrWhiteSpace([string]$Value.CompletionJournalId)) { throw 'Confirmed reconciliation evidence does not contain both NTFS journal boundaries.' }
            if ([int64]$Value.StartJournalNextUsn -gt [int64]$Value.CompletionJournalNextUsn) { throw 'Confirmed reconciliation journal boundaries are reversed.' }
            foreach ($field in @('SourceEventCount', 'CanonicalEventCount', 'FinalStateCount', 'LightweightEntryCount', 'MftEntryCount', 'CandidateCount', 'DetailedMetadataQueryCount', 'PrivilegeEnableSuccessCount', 'PrivilegeEnableFailureCount', 'AclFallbackCount', 'IoHintAttempts', 'IoHintSuccesses', 'IoHintFailures', 'LiveEventCount', 'LiveEventsDeduplicated', 'LiveEventsAccepted')) {
                if ([int64]$Value.$field -lt 0) { throw "Confirmed reconciliation contains a negative $field." }
            }
            if (-not [bool]$Value.BackgroundModeEnabled) { throw 'Confirmed reconciliation did not prove background mode was enabled.' }
            if ([int64]$Value.DetailedMetadataQueryCount -gt [int64]$Value.CandidateCount) { throw 'Confirmed reconciliation performed more detailed queries than candidates.' }
            if ([int64]$Value.IoHintSuccesses + [int64]$Value.IoHintFailures -gt [int64]$Value.IoHintAttempts) { throw 'Confirmed reconciliation I/O hint counters are inconsistent.' }
            if ([int64]$Value.CandidateCount -eq 0 -and [int64]$Value.DetailedMetadataQueryCount -ne 0) { throw 'Confirmed reconciliation performed detailed metadata queries without candidates.' }
            if ([int64]$Value.LiveEventsDeduplicated + [int64]$Value.LiveEventsAccepted -ne [int64]$Value.LiveEventCount) { throw 'Confirmed reconciliation live-event boundary counters are inconsistent.' }
            $expectedRatio = if ([int64]$Value.CandidateCount -eq 0) { 0d } else { [double]$Value.DetailedMetadataQueryCount / [double]$Value.CandidateCount }
            if ([math]::Abs([double]$Value.DetailedQueryCandidateRatio - $expectedRatio) -gt 0.000001) { throw 'Confirmed reconciliation detailed-query ratio does not match its counters.' }
            if ([double]$Value.ElapsedMilliseconds -lt 0) { throw 'Confirmed reconciliation elapsed time is negative.' }
        }
        'WindowsPrivileged' {
            if ($schema -ne 'StorageChronicle.WindowsPrivilegedAcceptance.v2') { throw 'Windows privileged evidence has an unexpected schema.' }
            if ([string]$Value.Configuration -ne 'Release') { throw 'Windows privileged evidence was not produced by the Release configuration.' }
            $required = @($Value.RequiredCapabilities)
            $tests = @($Value.Tests)
            if ($required.Count -ne $requiredWindowsPrivilegedCapabilities.Count -or
                (@($required | Sort-Object -Unique).Count -ne $required.Count) -or
                @($requiredWindowsPrivilegedCapabilities | Where-Object { $required -notcontains $_ }).Count -ne 0 -or
                @($required | Where-Object { $requiredWindowsPrivilegedCapabilities -notcontains $_ }).Count -ne 0) {
                throw 'Windows privileged evidence does not declare the complete required capability contract.'
            }
            if ($null -eq $Value.PSObject.Properties['Environment'] -or
                [string]$Value.Environment.ProductName -notmatch 'Windows 11' -or
                [string]$Value.Environment.Architecture -ne 'x64' -or
                -not [bool]$Value.Environment.IsAdministrator) { throw 'Windows privileged evidence does not prove an elevated Windows 11 x64 environment.' }
            if ([string]$Value.Status -ne 'PASSED') { throw 'Windows privileged evidence status is not PASSED.' }
            foreach ($capability in $requiredWindowsPrivilegedCapabilities) {
                $matches = @($tests | Where-Object { [string]$_.Capability -eq [string]$capability })
                if ($matches.Count -ne 1 -or [string]$matches[0].Status -ne 'PASSED') { throw "Windows privileged capability is not exactly PASSED: $capability" }
            }
            foreach ($artifactName in @('Environment', 'Capabilities', 'Oracle', 'SourceEventSummary', 'CanonicalSummary', 'FinalStateSummary', 'ReconciliationSummary', 'ConfirmedReconciliation', 'ServiceSummary', 'Errors', 'Result')) {
                $artifactProperty = $Value.Artifacts.PSObject.Properties[$artifactName]
                if ($null -eq $artifactProperty -or [string]::IsNullOrWhiteSpace([string]$artifactProperty.Value) -or -not (Test-Path -LiteralPath ([string]$artifactProperty.Value) -PathType Leaf)) { throw "Windows privileged artifact is missing: $artifactName" }
            }
            $capabilities = Read-ReferencedJson -Path ([string]$Value.Artifacts.Capabilities) -Label 'Windows privileged capability artifact'
            if ([string]$capabilities.Schema -ne 'StorageChronicle.WindowsPrivilegedCapabilities.v1' -or @($capabilities.Required).Count -ne $requiredWindowsPrivilegedCapabilities.Count -or @($capabilities.Tests | Where-Object { [string]$_.Status -ne 'PASSED' }).Count -ne 0) { throw 'Windows privileged capability artifact is not a complete passed result.' }
            $errors = Read-ReferencedJson -Path ([string]$Value.Artifacts.Errors) -Label 'Windows privileged error artifact'
            if ([string]$errors.Schema -ne 'StorageChronicle.WindowsPrivilegedErrors.v1' -or @($errors.Errors).Count -ne 0) { throw 'Windows privileged error artifact contains failures or is malformed.' }
            $result = Read-ReferencedJson -Path ([string]$Value.Artifacts.Result) -Label 'Windows privileged result artifact'
            if ([string]$result.Schema -ne 'StorageChronicle.WindowsPrivilegedAcceptance.v2' -or [string]$result.Status -ne 'PASSED' -or -not [bool]$result.AcceptanceEligible) { throw 'Windows privileged result artifact is not eligible.' }
            foreach ($artifactName in @('Oracle', 'SourceEventSummary', 'CanonicalSummary', 'FinalStateSummary', 'ReconciliationSummary', 'ServiceSummary')) {
                $payload = Read-ReferencedJson -Path ([string]$Value.Artifacts.$artifactName) -Label "Windows privileged $artifactName artifact"
                if ([string]$payload.Schema -ne "StorageChronicle.WindowsPrivileged.$artifactName.v1" -or [string]$payload.Status -ne 'PASSED' -or -not [bool]$payload.AcceptanceEligible) { throw "Windows privileged $artifactName artifact is a placeholder, failed, or ineligible result." }
                switch ($artifactName) {
                    'Oracle' {
                        if ([string]::IsNullOrWhiteSpace([string]$payload.OraclePath) -or -not (Test-Path -LiteralPath ([string]$payload.OraclePath) -PathType Leaf)) { throw 'Windows privileged Oracle artifact references a missing workload oracle.' }
                    }
                    { $_ -in @('SourceEventSummary', 'CanonicalSummary', 'FinalStateSummary') } {
                        if ([string]::IsNullOrWhiteSpace([string]$payload.HistoryPath) -or -not (Test-Path -LiteralPath ([string]$payload.HistoryPath) -PathType Container)) { throw "Windows privileged $artifactName artifact references a missing Agent history directory." }
                    }
                    'ReconciliationSummary' {
                        if ([string]::IsNullOrWhiteSpace([string]$payload.EvidencePath) -or -not (Test-Path -LiteralPath ([string]$payload.EvidencePath) -PathType Leaf)) { throw 'Windows privileged reconciliation summary references missing evidence.' }
                    }
                    'ServiceSummary' {
                        if ([string]::IsNullOrWhiteSpace([string]$payload.EvidencePath) -or -not (Test-Path -LiteralPath ([string]$payload.EvidencePath) -PathType Leaf)) { throw 'Windows privileged service summary references missing transcript evidence.' }
                    }
                }
            }
        }
        'Windows10_22H2' {
            if ($schema -ne 'StorageChronicle.Windows10PhysicalAcceptance.v1') { throw 'Windows 10 evidence has an unexpected schema.' }
            if ([string]$Value.TargetOs -ne 'Windows10-22H2') { throw 'Windows 10 evidence does not identify Windows10-22H2.' }
            foreach ($field in @('StageA', 'StageB')) { if ($null -eq $Value.PSObject.Properties[$field]) { throw "Windows 10 evidence is missing $field." } }
            $stageA = Read-ReferencedJson -Path ([string]$Value.StageA.ManifestPath) -Label 'Windows 10 Stage A manifest'
            if ([string]$stageA.Schema -ne 'StorageChronicle.Windows10StageAAcceptance.v1' -or [string]$stageA.TargetOs -ne 'Windows10-22H2' -or [string]$stageA.TargetKind -ne 'HyperVVm' -or [string]$stageA.VmName -ne 'SC-Test-W10' -or [string]$stageA.ExecutionMode -ne 'VM' -or [string]$stageA.Status -ne 'PASSED' -or -not [bool]$stageA.AcceptanceEligible) { throw 'Windows 10 Stage A is not an eligible real acceptance artifact.' }
            if (@($stageA.Checks).Count -ne $requiredWindows10StageAChecks.Count) { throw 'Windows 10 Stage A does not contain exactly the required check count.' }
            $stageACheckNames = @($stageA.Checks | ForEach-Object { [string]$_.Name })
            if (@($stageACheckNames | Sort-Object -Unique).Count -ne $requiredWindows10StageAChecks.Count -or @($requiredWindows10StageAChecks | Where-Object { $stageACheckNames -notcontains $_ }).Count -ne 0) { throw 'Windows 10 Stage A checks are missing, duplicated, or contain an unexpected name.' }
            foreach ($checkName in $requiredWindows10StageAChecks) {
                $matches = @($stageA.Checks | Where-Object { [string]$_.Name -eq $checkName })
                if ($matches.Count -ne 1 -or [string]$matches[0].Status -ne 'PASSED') { throw "Windows 10 Stage A check is not exactly PASSED: $checkName" }
                if ([string]$matches[0].Origin -ne 'real') { throw "Windows 10 Stage A check does not declare real evidence: $checkName" }
                $checkEvidence = @($matches[0].Evidence | ForEach-Object { [string]$_ })
                if ($checkEvidence.Count -eq 0 -or @($checkEvidence | Where-Object { [string]::IsNullOrWhiteSpace($_) -or -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -ne 0) { throw "Windows 10 Stage A check evidence is missing: $checkName" }
            }
            $preflight = Read-ReferencedJson -Path ([string]$Value.StageB.PreflightPath) -Label 'Windows 10 physical preflight'
            if ([string]$preflight.Schema -ne 'StorageChronicle.Windows10PhysicalPreflight.v1' -or -not [bool]$preflight.Ready -or @($preflight.Checks | Where-Object { [string]$_.Status -ne 'PASS' }).Count -ne 0) { throw 'Windows 10 physical preflight is not fully PASS.' }
            if ([string]$preflight.Environment.ProductName -notmatch 'Windows 10' -or ([string]$preflight.Environment.DisplayVersion -ne '22H2' -and [string]$preflight.Environment.Build -ne '19045') -or [string]$preflight.Environment.Architecture -ne 'x64') { throw 'Windows 10 physical preflight does not prove Windows 10 22H2 x64.' }
            $installer = Read-ReferencedJson -Path ([string]$Value.StageB.InstallerManifestPath) -Label 'Windows 10 physical installer manifest'
            if ([string]$installer.Schema -ne 'storage-chronicle.installer-acceptance.v1' -or [string]$installer.Status -ne 'PASSED' -or -not [bool]$installer.AcceptanceEligible -or [string]$installer.TargetOs -ne 'Windows10-22H2' -or [string]$installer.TargetKind -ne 'PhysicalMachine' -or [string]$installer.ExecutionMode -ne 'Local') { throw 'Windows 10 physical installer artifact is not eligible.' }
            Assert-InstallerCaseRows -Value $installer -Label 'Windows 10 physical installer artifact'
        }
        'IdleResource' {
            if ($schema -ne 'StorageChronicle.ResourceBudgetAcceptanceEvidence.v1') { throw 'Resource evidence has an unexpected schema.' }
            if ([string]$Value.ExecutionStatus -ne 'passed' -or
                [string]$Value.Configuration -ne 'Release' -or
                $null -eq $Value.PSObject.Properties['Environment'] -or
                [string]$Value.Environment.ProductName -notmatch 'Windows 11' -or
                [string]$Value.Environment.Architecture -ne 'x64' -or
                -not [bool]$Value.Environment.IsPhysicalMachine -or
                [bool]$Value.Environment.Diagnostic) { throw 'Resource evidence does not prove a non-diagnostic Windows 11 x64 physical-machine run.' }
            if ($null -eq $Value.PSObject.Properties['EvidenceChecks']) { throw 'Resource evidence has no supervised gate checks.' }
            foreach ($checkName in @('ResultFilePresent', 'ResultProcessIdsMatch', 'ResourceSampleCountMatches', 'ResourceSamplingComplete', 'ResourceSampleSpanComplete', 'DiskWriteCounterPresent', 'PrivateMemoryLimitNotExceeded', 'CpuLimitNotExceeded', 'QueueSamplingComplete', 'QueueSampleCountMatches', 'QueueHasNoMissedSamples', 'QueueLimitNotExceeded', 'TargetLifecycleStable', 'ExistingScriptExitCodeZero', 'AcceptanceEligible', 'BoundaryDurationIs600', 'BoundaryIsNonDiagnostic', 'BoundarySpanComplete', 'QuietPeriodEvidencePresent', 'QuietPeriodValid')) {
                if ($null -eq $Value.EvidenceChecks.PSObject.Properties[$checkName] -or -not [bool]$Value.EvidenceChecks.$checkName) { throw "Resource acceptance check is not true: $checkName" }
            }
        }
        'MftPerformance' {
            if ($schema -ne 'StorageChronicle.FullBenchmarkMatrixEvidence.v1') { throw 'MFT performance evidence has an unexpected schema.' }
            if (-not [bool]$Value.IncludeMft -or [string]$Value.Configuration -ne 'Release' -or $null -eq $Value.MftEvidence) { throw 'MFT performance evidence is missing the connected Release MFT correctness artifact.' }
            if ([string]$Value.ExecutionStatus -ne 'completed' -or [string]$Value.MftEvidence.Schema -ne 'StorageChronicle.MftBenchmarkEvidence.v1' -or [string]$Value.MftEvidence.Status -ne 'PASSED' -or -not [bool]$Value.MftEvidence.AcceptanceEligible -or $null -eq $Value.MftEvidence.PSObject.Properties['FailureReasons'] -or @($Value.MftEvidence.FailureReasons).Count -ne 0) { throw 'MFT performance evidence is not a completed eligible real matrix.' }
            if ([string]$Value.Host.MftVolumeLabel -ne 'SC_TEST_MFT_VOLUME' -or
                [string]::IsNullOrWhiteSpace([string]$Value.Host.MftMarkerPath) -or
                -not (Test-Path -LiteralPath ([string]$Value.Host.MftMarkerPath) -PathType Leaf) -or
                [string]$Value.Host.WindowsProductName -notmatch 'Windows 11' -or
                [string]::IsNullOrWhiteSpace([string]$Value.MftEvidencePath) -or
                -not (Test-Path -LiteralPath ([string]$Value.MftEvidencePath) -PathType Leaf)) { throw 'MFT performance evidence does not prove the dedicated Windows 11 TestLab volume and connected evidence path.' }
            $requiredSuites = [ordered]@{
                CoreProjectionState = @('ReconstructSinglePointPath1M', 'GroupedGeneration100K', 'EventStackPage100K', 'PeriodDiff100K')
                LargeFolderMove = @('RecordLargeFolderMove')
                AppendAndCompression = @('SegmentAppendAndSqliteIndex100K', 'FlushAndCloseCompressedSegment100K')
                SqliteRecoveryAndQuery = @('SqliteIndexRebuild100K', 'SqliteIndexedCount100K')
                MediaManifest = @('MediaManifestImport100K', 'MediaManifestReadAndValidate100K')
                MediaSegment = @('MediaSegmentAppend100K')
                WindowsMft = @('MftEnumerationImport10K', 'MftEnumerationImport100K', 'MftEnumerationImport1M', 'MftCandidateMetadataQueriesZero1M', 'MftCandidateMetadataQueriesSmall1M')
            }
            $suiteNames = @($Value.Suites | ForEach-Object { [string]$_.Name })
            if (@($Value.Suites).Count -ne $requiredSuites.Count -or @($suiteNames | Sort-Object -Unique).Count -ne $requiredSuites.Count) { throw 'MFT performance evidence does not contain exactly one result for each required suite.' }
            foreach ($suiteName in $requiredSuites.Keys) {
                $suiteMatches = @($Value.Suites | Where-Object { [string]$_.Name -eq $suiteName })
                if ($suiteMatches.Count -ne 1 -or [string]$suiteMatches[0].Status -ne 'passed') { throw "MFT performance suite is not exactly passed: $suiteName" }
                $actualMethods = @($suiteMatches[0].ActualMethods | ForEach-Object { [string]$_ })
                foreach ($method in $requiredSuites[$suiteName]) { if ($actualMethods -notcontains $method) { throw "MFT performance suite is missing method ${method}: $suiteName" } }
            }
            foreach ($method in @('MftEnumerationImport10K', 'MftEnumerationImport100K', 'MftEnumerationImport1M', 'MftCandidateMetadataQueriesZero1M', 'MftCandidateMetadataQueriesSmall1M')) {
                $matches = @($Value.MftEvidence.Runs | Where-Object { [string]$_.Method -eq $method })
                if ($matches.Count -ne 1) { throw "MFT evidence does not contain exactly one run for $method." }
                if ([int64]$matches[0].DroppedEventCount -ne 0 -or [int64]$matches[0].EnumeratedEntryCount -lt [int64]$matches[0].DatasetEntryCount) { throw "MFT evidence failed the count/drop oracle for $method." }
            }
            if ([int64](@($Value.MftEvidence.Runs | Where-Object Method -eq 'MftEnumerationImport1M')[0].DatasetEntryCount) -lt 1000000) { throw 'MFT evidence does not contain the required one-million-entry dataset.' }
        }
        'PhysicalInstaller' {
            if ($schema -ne 'storage-chronicle.installer-acceptance.v1') { throw 'Installer evidence has an unexpected schema.' }
            if ([string]$Value.Status -ne 'PASSED') { throw 'Installer evidence status is not PASSED.' }
            if ([string]$Value.TargetOs -ne 'Windows11' -or [string]$Value.TargetKind -ne 'PhysicalMachine' -or [string]$Value.ExecutionMode -ne 'Local') { throw 'Installer evidence is not from the required Windows 11 physical-machine acceptance path.' }
            Assert-InstallerCaseRows -Value $Value -Label 'Windows 11 physical installer evidence'
            Assert-Windows11HyperVInstallerPrerequisite -Path $Windows11HyperVInstallerManifest
        }
        'AgentExplorerCorrelation' {
            if ($schema -ne 'StorageChronicle.AgentExplorerCorrelationEvidence.v1') { throw 'Agent/Explorer correlation evidence has an unexpected schema.' }
            if ([string]$Value.Status -ne 'PASSED' -or [string]$Value.LiveMachineMeasurement -ne 'PASSED') { throw 'Agent/Explorer evidence is not a live machine measurement.' }
            if ($null -eq $Value.PSObject.Properties['FalseExactCount'] -or [int]$Value.FalseExactCount -ne 0) { throw 'Agent/Explorer evidence does not prove false Exact attribution is zero.' }
            foreach ($field in @('ProcessAttribution', 'ExplorerSourceCorrelation', 'FileStateCorrectness', 'WorkloadOraclePath', 'Environment', 'SourceEventCount', 'CanonicalEventCount', 'FinalStateCount', 'Failures')) { if ($null -eq $Value.PSObject.Properties[$field]) { throw "Agent/Explorer evidence is missing $field." } }
            if ([string]$Value.Environment.TargetOs -ne 'Windows11' -or
                [string]$Value.Environment.VmName -ne 'SC-Test-W11' -or
                [string]$Value.Environment.ExecutionMode -ne 'TestLab' -or
                [string]$Value.Environment.AgentHostMode -ne 'TestLab' -or
                [bool]$Value.Environment.Diagnostic) { throw 'Agent/Explorer evidence does not prove a non-diagnostic Windows 11 TestLab Agent run.' }
            foreach ($path in @([string]$Value.WorkloadOraclePath, [string]$Value.Environment.AgentExecutablePath, [string]$Value.Environment.WorkloadExecutablePath, [string]$Value.Environment.WorkloadOraclePath, [string]$Value.Environment.ExplorerEvidencePath)) {
                if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Agent/Explorer evidence references a missing real artifact: $path" }
            }
            if ([string]::IsNullOrWhiteSpace([string]$Value.Environment.AgentHistoryPath) -or -not (Test-Path -LiteralPath ([string]$Value.Environment.AgentHistoryPath) -PathType Container)) { throw "Agent/Explorer evidence references a missing Agent history directory: $($Value.Environment.AgentHistoryPath)" }
            if (-not [string]::Equals([IO.Path]::GetFullPath([string]$Value.WorkloadOraclePath), [IO.Path]::GetFullPath([string]$Value.Environment.WorkloadOraclePath), [StringComparison]::OrdinalIgnoreCase)) { throw 'Agent/Explorer workload oracle paths are inconsistent.' }
            if ([int]$Value.SourceEventCount -le 0 -or [int]$Value.CanonicalEventCount -le 0 -or [int]$Value.FinalStateCount -le 0 -or @($Value.Failures).Count -ne 0) { throw 'Agent/Explorer evidence does not contain complete durable counts or an empty failure list.' }
            foreach ($field in @('Total', 'Exact', 'Correlated', 'Unknown', 'FalseExactCount', 'ExactRate', 'CorrelatedRate', 'UnknownRate', 'Rows')) {
                if ($null -eq $Value.ProcessAttribution.PSObject.Properties[$field]) { throw "Agent process attribution is missing $field." }
            }
            if ([int]$Value.ProcessAttribution.Total -le 0 -or
                [int]$Value.ProcessAttribution.Exact -lt 0 -or
                [int]$Value.ProcessAttribution.Correlated -lt 0 -or
                [int]$Value.ProcessAttribution.Unknown -lt 0 -or
                [int]$Value.ProcessAttribution.Exact + [int]$Value.ProcessAttribution.Correlated + [int]$Value.ProcessAttribution.Unknown -ne [int]$Value.ProcessAttribution.Total -or
                [int]$Value.ProcessAttribution.FalseExactCount -ne 0 -or
                @($Value.ProcessAttribution.Rows).Count -ne [int]$Value.ProcessAttribution.Total) { throw 'Agent process attribution counts or rows are inconsistent.' }
            foreach ($field in @('CopyIntentCount', 'SourceCorrelatedCount', 'SourceUnknownCount', 'NotIdentifiedCount', 'FalseAttributionCount', 'Rows')) {
                if ($null -eq $Value.ExplorerSourceCorrelation.PSObject.Properties[$field]) { throw "Explorer correlation is missing $field." }
            }
            if ([int]$Value.ExplorerSourceCorrelation.CopyIntentCount -le 0 -or
                [int]$Value.ExplorerSourceCorrelation.SourceCorrelatedCount -lt 0 -or
                [int]$Value.ExplorerSourceCorrelation.SourceUnknownCount -lt 0 -or
                [int]$Value.ExplorerSourceCorrelation.NotIdentifiedCount -lt 0 -or
                [int]$Value.ExplorerSourceCorrelation.FalseAttributionCount -ne 0 -or
                [int]$Value.ExplorerSourceCorrelation.SourceCorrelatedCount + [int]$Value.ExplorerSourceCorrelation.SourceUnknownCount + [int]$Value.ExplorerSourceCorrelation.NotIdentifiedCount -ne [int]$Value.ExplorerSourceCorrelation.CopyIntentCount -or
                @($Value.ExplorerSourceCorrelation.Rows).Count -ne [int]$Value.ExplorerSourceCorrelation.CopyIntentCount) { throw 'Explorer correlation counts or rows are inconsistent.' }
            foreach ($field in @('ExpectedCount', 'VerifiedCount', 'MissingCount', 'DroppedEventCount')) {
                if ($null -eq $Value.FileStateCorrectness.PSObject.Properties[$field]) { throw "File/state correctness is missing $field." }
            }
            if ([int]$Value.FileStateCorrectness.ExpectedCount -le 0 -or
                [int]$Value.FileStateCorrectness.VerifiedCount -ne [int]$Value.FileStateCorrectness.ExpectedCount -or
                [int]$Value.FileStateCorrectness.MissingCount -ne 0 -or
                [int]$Value.FileStateCorrectness.DroppedEventCount -ne 0) { throw 'File/state correctness is not complete.' }
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
