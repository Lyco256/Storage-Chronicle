[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifact = Join-Path $root 'artifacts/quality/coverage'
$runs = Join-Path $artifact ('runs-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $runs | Out-Null

$globalJsonPath = Join-Path $root 'global.json'
$globalJson = if (Test-Path $globalJsonPath) { Get-Content $globalJsonPath -Raw | ConvertFrom-Json } else { $null }
if ($null -eq $globalJson -or $globalJson.test.runner -ne 'Microsoft.Testing.Platform') {
    Write-Error 'CodeCoverage requires global.json test.runner=Microsoft.Testing.Platform under the .NET 10 SDK.'
    exit 5
}

$projects = Get-ChildItem (Join-Path $root 'tests') -Recurse -Filter '*.Tests.csproj' |
    Where-Object { $_.FullName -notmatch 'StorageChronicle\.Platform\.Windows\.Integration\.Tests\.csproj$' } |
    Where-Object { $_.BaseName -in @(
        'StorageChronicle.Architecture.Tests',
        'StorageChronicle.Domain.Tests',
        'StorageChronicle.EndToEnd.Tests',
        'StorageChronicle.Integration.Tests',
        'StorageChronicle.Projection.Tests',
        'StorageChronicle.State.Tests',
        'StorageChronicle.Storage.Tests',
        'StorageChronicle.UI.DiffView.Tests',
        'StorageChronicle.UI.EventStack.Tests',
        'StorageChronicle.UI.Headless.Tests',
        'StorageChronicle.UI.Settings.Tests'
    ) } |
    Sort-Object FullName
foreach ($project in $projects) {
    $name = $project.BaseName
    $runRoot = Join-Path $runs $name
    $output = Join-Path $runRoot ($name + '.cobertura.xml')
    New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
    $log = Join-Path $runRoot ($name + '.log')
    $buildLog = Join-Path $runRoot ($name + '.build.log')
    & dotnet build $project.FullName -c $Configuration --no-restore --nologo 2>&1 | Tee-Object -FilePath $buildLog
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Coverage build failed: $name (exit code $LASTEXITCODE)"
        exit $LASTEXITCODE
    }
    $assembly = Get-ChildItem (Join-Path $project.Directory.FullName "bin\$Configuration") -Recurse -File -Filter ($name + '.dll') |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $assembly) {
        Write-Error "Coverage test assembly was not produced: $name"
        exit 6
    }
    $assemblyArgument = $assembly.FullName.Substring($root.Length + 1)
    $args = @(
        'test', $assemblyArgument, '--no-restore',
        '--results-directory', $runRoot, '--coverage',
        '--coverage-output', $output, '--coverage-output-format', 'cobertura'
    )
    Write-Host "Running coverage for $name"
    & dotnet @args 2>&1 | Tee-Object -FilePath $log
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Coverage test project failed: $name (exit code $LASTEXITCODE)"
        exit $LASTEXITCODE
    }
    if (-not (Test-Path $output)) {
        Write-Error "Coverage output was not produced by $($name): $output"
        exit 6
    }
}

$lines = @{}
Write-Host "Aggregating coverage reports under $runs"
foreach ($report in (Get-ChildItem $runs -Recurse -Filter '*.cobertura.xml' | Sort-Object FullName)) {
    $xml = [xml](Get-Content $report.FullName -Raw)
    foreach ($package in @($xml.coverage.packages.package)) {
        foreach ($class in @($package.classes.class)) {
            foreach ($line in @($class.lines.line)) {
                $key = '{0}|{1}|{2}' -f $package.name, $class.filename, $line.number
                $hit = ([int]$line.hits -gt 0)
                if (-not $lines.ContainsKey($key) -or $hit) { $lines[$key] = $hit }
            }
        }
    }
}

$packageRates = @{}
foreach ($packageName in ($lines.Keys | ForEach-Object { ($_ -split '\|', 2)[0] } | Sort-Object -Unique)) {
    $packageLines = @($lines.GetEnumerator() | Where-Object { $_.Key.StartsWith($packageName + '|', [StringComparison]::Ordinal) })
    $covered = @($packageLines | Where-Object Value).Count
    $total = $packageLines.Count
    $packageRates[$packageName] = if ($total -eq 0) { 0.0 } else { [double]$covered / $total }
}

$failures = [System.Collections.Generic.List[string]]::new()
$requiredPackages = @{
    'StorageChronicle.Domain' = 0.80
    'StorageChronicle.State' = 0.80
    'StorageChronicle.Projection' = 0.80
    'StorageChronicle.Storage' = 0.80
}
foreach ($required in $requiredPackages.GetEnumerator()) {
    if (-not $packageRates.ContainsKey($required.Key)) {
        [void]$failures.Add("missing package coverage: $($required.Key)")
        continue
    }
    $rate = $packageRates[$required.Key]
    if ($rate -lt $required.Value) {
        [void]$failures.Add("$($required.Key) coverage $([math]::Round($rate * 100, 2))% is below $([int]($required.Value * 100))%")
    }
}

$viewModelRates = @{}
foreach ($key in $lines.Keys) {
    $parts = $key -split '\|', 3
    if ($parts.Count -ne 3 -or $parts[0] -notlike 'StorageChronicle.UI.*') { continue }
    $className = [IO.Path]::GetFileNameWithoutExtension($parts[1])
    if ($className -notmatch 'ViewModel') { continue }
    $classKey = "$($parts[0])|$($parts[1])"
    if (-not $viewModelRates.ContainsKey($classKey)) { $viewModelRates[$classKey] = @{ Total = 0; Covered = 0 } }
    $viewModelRates[$classKey].Total++
    if ($lines[$key]) { $viewModelRates[$classKey].Covered++ }
}
foreach ($entry in $viewModelRates.GetEnumerator()) {
    $rate = if ($entry.Value.Total -eq 0) { 0.0 } else { [double]$entry.Value.Covered / $entry.Value.Total }
    if ($rate -lt 0.70) {
        [void]$failures.Add("$($entry.Key) coverage $([math]::Round($rate * 100, 2))% is below 70%")
    }
}

$reportPath = Join-Path $artifact 'coverage-summary.json'
$summary = [ordered]@{
    Configuration = $Configuration
    TestProjects = $projects.Count
    PackageRates = $packageRates
    ViewModelRates = @{}
    Failures = @($failures)
}
foreach ($entry in $viewModelRates.GetEnumerator()) {
    $summary.ViewModelRates[$entry.Key] = [math]::Round(([double]$entry.Value.Covered / [math]::Max(1, $entry.Value.Total)) * 100, 2)
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -Path $reportPath -Encoding UTF8
Write-Host "Coverage summary written to $reportPath"
foreach ($entry in $packageRates.GetEnumerator()) { Write-Host "$($entry.Key): $([math]::Round($entry.Value * 100, 2))%" }
foreach ($entry in $viewModelRates.GetEnumerator()) { Write-Host "$($entry.Key): $([math]::Round(([double]$entry.Value.Covered / [math]::Max(1, $entry.Value.Total)) * 100, 2))%" }
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 7
}
exit 0
