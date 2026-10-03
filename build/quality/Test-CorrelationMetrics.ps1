[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$FixturePath,
    [string]$OutputPath,
    [string]$LiveEvidencePath,
    [switch]$FixtureOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $PSScriptRoot 'AcceptanceContracts.ps1')
$project = Join-Path $root 'tests/StorageChronicle.CorrelationAcceptance.Tests/StorageChronicle.CorrelationAcceptance.Tests.csproj'
$defaultFixture = Join-Path $root 'tests/StorageChronicle.CorrelationAcceptance.Tests/Fixtures/r00-process-explorer-correlation.json'
$artifactDirectory = Join-Path $root 'artifacts/quality/correlation'
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

if ([string]::IsNullOrWhiteSpace($FixturePath)) { $FixturePath = $defaultFixture }
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $artifactDirectory ('correlation-metrics-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N') + '.json')
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (-not [string]::IsNullOrWhiteSpace($LiveEvidencePath) -and $FixtureOnly) {
    throw '-LiveEvidencePath and -FixtureOnly cannot be combined.'
}
$logPath = [IO.Path]::ChangeExtension($OutputPath, '.log')
$logPath = [IO.Path]::GetFullPath($logPath)
if ([string]::Equals($OutputPath, $logPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputPath must not also be the log path.' }
if ((Test-Path -LiteralPath $OutputPath) -or (Test-Path -LiteralPath $logPath)) { throw 'Correlation evidence or log output already exists; choose new paths and preserve prior artifacts.' }
$rawReportPath = Join-Path (Split-Path -Parent $OutputPath) ('.' + [IO.Path]::GetFileNameWithoutExtension($OutputPath) + '.' + [guid]::NewGuid().ToString('N') + '.raw.json')
$logLines = [System.Collections.Generic.List[string]]::new()

function Write-NotExecuted([string]$Reason) {
    $directory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
    if (-not [string]::IsNullOrWhiteSpace($directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    $manifest = [ordered]@{
        Status = 'NOT_EXECUTED'
        FixtureStatus = 'NOT_EXECUTED'
        LiveMachineMeasurement = 'NOT_EXECUTED'
        Requirements = @('R-00 section 8', 'R-00 section 9')
        FixturePath = $FixturePath
        Reason = $Reason
    }
    Write-NewJsonArtifact -Path $OutputPath -Value $manifest -Depth 8
    Write-Host "CORRELATION_METRICS status=NOT_EXECUTED reason=$Reason" -ForegroundColor Yellow
    exit 2
}

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { Write-NotExecuted "Acceptance test project is missing: $project" }
if (-not (Test-Path -LiteralPath $FixturePath -PathType Leaf)) { Write-NotExecuted "Deterministic correlation fixture is missing: $FixturePath" }

$oldFixture = [Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_CORRELATION_FIXTURE', 'Process')
$oldReport = [Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_CORRELATION_REPORT', 'Process')
$exitCode = 1
try {
    $buildOutput = @(& dotnet build $project --configuration $Configuration --no-restore --nologo 2>&1)
    $buildExitCode = $LASTEXITCODE
    foreach ($line in $buildOutput) { [void]$logLines.Add([string]$line); Write-Output $line }
    if ($buildExitCode -ne 0) {
        $exitCode = $buildExitCode
        Write-NewTextArtifact -Path $logPath -Text ($logLines -join [Environment]::NewLine)
        throw "Correlation acceptance project build failed with exit code $exitCode."
    }
    $assemblyName = [IO.Path]::GetFileNameWithoutExtension($project)
    $testExecutable = Get-ChildItem (Join-Path (Split-Path -Parent $project) "bin\$Configuration") -Recurse -File -Filter ($assemblyName + '.exe') |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $testExecutable) { throw 'Correlation acceptance test executable was not produced.' }
    $env:STORAGE_CHRONICLE_CORRELATION_FIXTURE = [IO.Path]::GetFullPath($FixturePath)
    $env:STORAGE_CHRONICLE_CORRELATION_REPORT = $rawReportPath
    $testOutput = @(& $testExecutable.FullName --progress off --minimum-expected-tests 1 2>&1)
    $exitCode = $LASTEXITCODE
    foreach ($line in $testOutput) { [void]$logLines.Add([string]$line); Write-Output $line }
    Write-NewTextArtifact -Path $logPath -Text ($logLines -join [Environment]::NewLine)
}
finally {
    if ($null -eq $oldFixture) { Remove-Item Env:STORAGE_CHRONICLE_CORRELATION_FIXTURE -ErrorAction SilentlyContinue } else { $env:STORAGE_CHRONICLE_CORRELATION_FIXTURE = $oldFixture }
    if ($null -eq $oldReport) { Remove-Item Env:STORAGE_CHRONICLE_CORRELATION_REPORT -ErrorAction SilentlyContinue } else { $env:STORAGE_CHRONICLE_CORRELATION_REPORT = $oldReport }
}

if ($exitCode -ne 0) {
    Write-Error "Correlation acceptance fixture failed. ExitCode=$exitCode. See $logPath"
    exit $exitCode
}

if (-not [string]::IsNullOrWhiteSpace($LiveEvidencePath)) {
    if (-not (Test-Path -LiteralPath $LiveEvidencePath -PathType Leaf)) { Write-NotExecuted "Live correlation evidence was not found: $LiveEvidencePath" }
    $live = Get-Content -Raw -Encoding UTF8 -LiteralPath $LiveEvidencePath | ConvertFrom-Json
    if ([string]$live.Schema -ne 'StorageChronicle.AgentExplorerCorrelationEvidence.v1' -or
        [string]$live.Status -ne 'PASSED' -or
        -not [bool]$live.AcceptanceEligible -or
        [string]$live.LiveMachineMeasurement -ne 'PASSED' -or
        $null -eq $live.PSObject.Properties['FalseExactCount'] -or
        [int]$live.FalseExactCount -ne 0 -or
        [string]$live.Environment.TargetOs -ne 'Windows11' -or
        [string]$live.Environment.TargetKind -ne 'PhysicalMachine' -or
        [string]$live.Environment.ExecutionMode -ne 'Local' -or
        [string]$live.Environment.AgentHostMode -ne 'Service' -or
        [string]::IsNullOrWhiteSpace([string]$live.Environment.ComputerName) -or
        [string]$live.Environment.RunId -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' -or
        [bool]$live.Environment.Diagnostic) {
        Write-Error 'The supplied live correlation artifact is missing the required physical Windows 11 Service identity, run identity, or zero-false-Exact proof.'
        exit 1
    }
    foreach ($path in @([string]$live.WorkloadOraclePath, [string]$live.Environment.AgentExecutablePath, [string]$live.Environment.WorkloadExecutablePath, [string]$live.Environment.WorkloadOraclePath, [string]$live.Environment.ExplorerEvidencePath)) {
        if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { Write-Error "The supplied live correlation artifact references a missing file: $path"; exit 1 }
    }
    if ([string]::IsNullOrWhiteSpace([string]$live.Environment.AgentHistoryPath) -or -not (Test-Path -LiteralPath ([string]$live.Environment.AgentHistoryPath) -PathType Container)) { Write-Error "The supplied live correlation artifact references a missing Agent history directory: $($live.Environment.AgentHistoryPath)"; exit 1 }
    foreach ($field in @('SourceEventCount', 'CanonicalEventCount', 'FinalStateCount', 'Failures')) {
        if ($null -eq $live.PSObject.Properties[$field]) { Write-Error "The supplied live correlation artifact is missing durable evidence field: $field"; exit 1 }
    }
    if (-not [string]::Equals([IO.Path]::GetFullPath([string]$live.WorkloadOraclePath), [IO.Path]::GetFullPath([string]$live.Environment.WorkloadOraclePath), [StringComparison]::OrdinalIgnoreCase) -or
        [int]$live.SourceEventCount -le 0 -or [int]$live.CanonicalEventCount -le 0 -or [int]$live.FinalStateCount -le 0 -or @($live.Failures).Count -ne 0) {
        Write-Error 'The supplied live correlation artifact has inconsistent durable paths/counts or recorded failures.'; exit 1
    }
    foreach ($field in @('Total', 'Exact', 'Correlated', 'Unknown', 'FalseExactCount', 'ExactRate', 'CorrelatedRate', 'UnknownRate', 'Rows')) {
        if ($null -eq $live.ProcessAttribution.PSObject.Properties[$field]) { Write-Error "The supplied live correlation artifact is missing process field: $field"; exit 1 }
    }
    if ([int]$live.ProcessAttribution.Total -le 0 -or [int]$live.ProcessAttribution.Exact + [int]$live.ProcessAttribution.Correlated + [int]$live.ProcessAttribution.Unknown -ne [int]$live.ProcessAttribution.Total -or [int]$live.ProcessAttribution.FalseExactCount -ne 0 -or @($live.ProcessAttribution.Rows).Count -ne [int]$live.ProcessAttribution.Total) { Write-Error 'The supplied live process attribution rows/counts are inconsistent.'; exit 1 }
    foreach ($field in @('CopyIntentCount', 'SourceCorrelatedCount', 'SourceUnknownCount', 'NotIdentifiedCount', 'FalseAttributionCount', 'Rows')) {
        if ($null -eq $live.ExplorerSourceCorrelation.PSObject.Properties[$field]) { Write-Error "The supplied live correlation artifact is missing Explorer field: $field"; exit 1 }
    }
    if ([int]$live.ExplorerSourceCorrelation.CopyIntentCount -le 0 -or [int]$live.ExplorerSourceCorrelation.FalseAttributionCount -ne 0 -or [int]$live.ExplorerSourceCorrelation.SourceCorrelatedCount + [int]$live.ExplorerSourceCorrelation.SourceUnknownCount + [int]$live.ExplorerSourceCorrelation.NotIdentifiedCount -ne [int]$live.ExplorerSourceCorrelation.CopyIntentCount -or @($live.ExplorerSourceCorrelation.Rows).Count -ne [int]$live.ExplorerSourceCorrelation.CopyIntentCount) { Write-Error 'The supplied live Explorer attribution rows/counts are inconsistent.'; exit 1 }
    foreach ($field in @('ExpectedCount', 'VerifiedCount', 'MissingCount', 'DroppedEventCount')) {
        if ($null -eq $live.FileStateCorrectness.PSObject.Properties[$field]) { Write-Error "The supplied live correlation artifact is missing correctness field: $field"; exit 1 }
    }
    if ([int]$live.FileStateCorrectness.ExpectedCount -le 0 -or [int]$live.FileStateCorrectness.VerifiedCount -ne [int]$live.FileStateCorrectness.ExpectedCount -or [int]$live.FileStateCorrectness.MissingCount -ne 0 -or [int]$live.FileStateCorrectness.DroppedEventCount -ne 0) { Write-Error 'The supplied live file/state correctness counts are incomplete.'; exit 1 }
    $live | Add-Member -NotePropertyName FixtureStatus -NotePropertyValue 'PASSED' -Force
    $live | Add-Member -NotePropertyName Status -NotePropertyValue 'PASSED' -Force
    Write-NewJsonArtifact -Path $OutputPath -Value $live -Depth 20
    Write-Host "CORRELATION_METRICS live=PASSED evidence=$LiveEvidencePath" -ForegroundColor Green
    Write-Host "Correlation report: $OutputPath" -ForegroundColor Cyan
    exit 0
}
if (-not (Test-Path -LiteralPath $rawReportPath -PathType Leaf)) { Write-NotExecuted "The acceptance test passed without producing its measurement report: $rawReportPath" }

$report = Get-Content -Raw -Encoding UTF8 -LiteralPath $rawReportPath | ConvertFrom-Json
if ($report.FixtureId -ne 'r00-process-explorer-correlation-v1' -or
    $report.ProcessAttribution.Total -ne 3 -or
    $report.ProcessAttribution.Exact -ne 1 -or
    $report.ProcessAttribution.Correlated -ne 1 -or
    $report.ProcessAttribution.Unknown -ne 1 -or
    $report.ExplorerSourceCorrelation.ExplorerCandidates -ne 3 -or
    $report.ExplorerSourceCorrelation.Correlated -ne 1 -or
    $report.ExplorerSourceCorrelation.Uncorrelated -ne 2) {
    Write-Error "The correlation report does not match the fixed golden expectations: $OutputPath"
    exit 1
}

$report | Add-Member -NotePropertyName FixtureStatus -NotePropertyValue 'PASSED' -Force
$report | Add-Member -NotePropertyName LiveMachineMeasurement -NotePropertyValue 'NOT_EXECUTED' -Force
$report | Add-Member -NotePropertyName Requirements -NotePropertyValue @('R-00 section 8', 'R-00 section 9') -Force
if ($FixtureOnly) {
    $report | Add-Member -NotePropertyName Status -NotePropertyValue 'PASSED_FIXTURE_ONLY' -Force
    $report | Add-Member -NotePropertyName AcceptanceNote -NotePropertyValue 'Deterministic metadata-only fixture passed. No live Agent/Session/Explorer capture is claimed.' -Force
    Write-NewJsonArtifact -Path $OutputPath -Value $report -Depth 12
    Write-Host "CORRELATION_METRICS fixture=PASSED process=Exact:$($report.ProcessAttribution.Exact)/$($report.ProcessAttribution.Total),Correlated:$($report.ProcessAttribution.Correlated)/$($report.ProcessAttribution.Total),Unknown:$($report.ProcessAttribution.Unknown)/$($report.ProcessAttribution.Total) explorer=$($report.ExplorerSourceCorrelation.Correlated)/$($report.ExplorerSourceCorrelation.ExplorerCandidates) rate=$($report.ExplorerSourceCorrelation.CorrelationRate)" -ForegroundColor Green
    Write-Host 'LIVE_MACHINE_MEASUREMENT=NOT_EXECUTED (fixture-only mode)' -ForegroundColor Yellow
    Write-Host "Correlation report: $OutputPath" -ForegroundColor Cyan
    exit 0
}

$report | Add-Member -NotePropertyName Status -NotePropertyValue 'NOT_EXECUTED' -Force
$report | Add-Member -NotePropertyName AcceptanceNote -NotePropertyValue 'The deterministic fixture passed, but live process/Explorer measurement requires a running Agent and interactive Session Agent on an acceptance machine. It was not supplied.' -Force
Write-NewJsonArtifact -Path $OutputPath -Value $report -Depth 12
Write-Host "CORRELATION_METRICS fixture=PASSED process=Exact:$($report.ProcessAttribution.Exact)/$($report.ProcessAttribution.Total),Correlated:$($report.ProcessAttribution.Correlated)/$($report.ProcessAttribution.Total),Unknown:$($report.ProcessAttribution.Unknown)/$($report.ProcessAttribution.Total) explorer=$($report.ExplorerSourceCorrelation.Correlated)/$($report.ExplorerSourceCorrelation.ExplorerCandidates) rate=$($report.ExplorerSourceCorrelation.CorrelationRate)" -ForegroundColor Green
Write-Host 'LIVE_MACHINE_MEASUREMENT=NOT_EXECUTED; overall status is NOT_EXECUTED.' -ForegroundColor Yellow
Write-Host "Correlation report: $OutputPath" -ForegroundColor Cyan
exit 2
