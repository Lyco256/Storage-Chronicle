[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$FixturePath,
    [string]$OutputPath,
    [switch]$FixtureOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $root 'tests/StorageChronicle.CorrelationAcceptance.Tests/StorageChronicle.CorrelationAcceptance.Tests.csproj'
$defaultFixture = Join-Path $root 'tests/StorageChronicle.CorrelationAcceptance.Tests/Fixtures/r00-process-explorer-correlation.json'
$artifactDirectory = Join-Path $root 'artifacts/quality/correlation'
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

if ([string]::IsNullOrWhiteSpace($FixturePath)) { $FixturePath = $defaultFixture }
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $artifactDirectory ('correlation-metrics-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
}
$logPath = [IO.Path]::ChangeExtension($OutputPath, '.log')

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
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath $OutputPath
    Write-Host "CORRELATION_METRICS status=NOT_EXECUTED reason=$Reason" -ForegroundColor Yellow
    exit 2
}

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { Write-NotExecuted "Acceptance test project is missing: $project" }
if (-not (Test-Path -LiteralPath $FixturePath -PathType Leaf)) { Write-NotExecuted "Deterministic correlation fixture is missing: $FixturePath" }

$oldFixture = [Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_CORRELATION_FIXTURE', 'Process')
$oldReport = [Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_CORRELATION_REPORT', 'Process')
$exitCode = 1
try {
    & dotnet build $project --configuration $Configuration --no-restore --nologo 2>&1 | Tee-Object -FilePath $logPath
    if ($LASTEXITCODE -ne 0) {
        $exitCode = $LASTEXITCODE
        throw "Correlation acceptance project build failed with exit code $exitCode."
    }
    $assemblyName = [IO.Path]::GetFileNameWithoutExtension($project)
    $testExecutable = Get-ChildItem (Join-Path (Split-Path -Parent $project) "bin\$Configuration") -Recurse -File -Filter ($assemblyName + '.exe') |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $testExecutable) { throw 'Correlation acceptance test executable was not produced.' }
    $env:STORAGE_CHRONICLE_CORRELATION_FIXTURE = [IO.Path]::GetFullPath($FixturePath)
    $env:STORAGE_CHRONICLE_CORRELATION_REPORT = [IO.Path]::GetFullPath($OutputPath)
    & $testExecutable.FullName --progress off --minimum-expected-tests 1 2>&1 | Tee-Object -FilePath $logPath
    $exitCode = $LASTEXITCODE
}
finally {
    if ($null -eq $oldFixture) { Remove-Item Env:STORAGE_CHRONICLE_CORRELATION_FIXTURE -ErrorAction SilentlyContinue } else { $env:STORAGE_CHRONICLE_CORRELATION_FIXTURE = $oldFixture }
    if ($null -eq $oldReport) { Remove-Item Env:STORAGE_CHRONICLE_CORRELATION_REPORT -ErrorAction SilentlyContinue } else { $env:STORAGE_CHRONICLE_CORRELATION_REPORT = $oldReport }
}

if ($exitCode -ne 0) {
    Write-Error "Correlation acceptance fixture failed. ExitCode=$exitCode. See $logPath"
    exit $exitCode
}
if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) { Write-NotExecuted "The acceptance test passed without producing its measurement report: $OutputPath" }

$report = Get-Content -Raw -Encoding UTF8 -LiteralPath $OutputPath | ConvertFrom-Json
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
    $report | ConvertTo-Json -Depth 12 | Set-Content -Encoding UTF8 -LiteralPath $OutputPath
    Write-Host "CORRELATION_METRICS fixture=PASSED process=Exact:$($report.ProcessAttribution.Exact)/$($report.ProcessAttribution.Total),Correlated:$($report.ProcessAttribution.Correlated)/$($report.ProcessAttribution.Total),Unknown:$($report.ProcessAttribution.Unknown)/$($report.ProcessAttribution.Total) explorer=$($report.ExplorerSourceCorrelation.Correlated)/$($report.ExplorerSourceCorrelation.ExplorerCandidates) rate=$($report.ExplorerSourceCorrelation.CorrelationRate)" -ForegroundColor Green
    Write-Host 'LIVE_MACHINE_MEASUREMENT=NOT_EXECUTED (fixture-only mode)' -ForegroundColor Yellow
    Write-Host "Correlation report: $OutputPath" -ForegroundColor Cyan
    exit 0
}

$report | Add-Member -NotePropertyName Status -NotePropertyValue 'NOT_EXECUTED' -Force
$report | Add-Member -NotePropertyName AcceptanceNote -NotePropertyValue 'The deterministic fixture passed, but live process/Explorer measurement requires a running Agent and interactive Session Agent on an acceptance machine. It was not supplied.' -Force
$report | ConvertTo-Json -Depth 12 | Set-Content -Encoding UTF8 -LiteralPath $OutputPath
Write-Host "CORRELATION_METRICS fixture=PASSED process=Exact:$($report.ProcessAttribution.Exact)/$($report.ProcessAttribution.Total),Correlated:$($report.ProcessAttribution.Correlated)/$($report.ProcessAttribution.Total),Unknown:$($report.ProcessAttribution.Unknown)/$($report.ProcessAttribution.Total) explorer=$($report.ExplorerSourceCorrelation.Correlated)/$($report.ExplorerSourceCorrelation.ExplorerCandidates) rate=$($report.ExplorerSourceCorrelation.CorrelationRate)" -ForegroundColor Green
Write-Host 'LIVE_MACHINE_MEASUREMENT=NOT_EXECUTED; overall status is NOT_EXECUTED.' -ForegroundColor Yellow
Write-Host "Correlation report: $OutputPath" -ForegroundColor Cyan
exit 2
