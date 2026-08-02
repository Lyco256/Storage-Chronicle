param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifact = Join-Path $root 'artifacts/quality/coverage'
New-Item -ItemType Directory -Force -Path $artifact | Out-Null
$globalJsonPath = Join-Path $root 'global.json'
$globalJson = if (Test-Path $globalJsonPath) { Get-Content $globalJsonPath -Raw | ConvertFrom-Json } else { $null }
if ($null -eq $globalJson -or $globalJson.test.runner -ne 'Microsoft.Testing.Platform') {
    Write-Error 'CodeCoverage requires global.json test.runner=Microsoft.Testing.Platform under the .NET 10 SDK.'
    exit 5
}
$projects = @(
    'tests/StorageChronicle.Architecture.Tests/StorageChronicle.Architecture.Tests.csproj',
    'tests/StorageChronicle.EndToEnd.Tests/StorageChronicle.EndToEnd.Tests.csproj',
    'tests/StorageChronicle.UI.Headless.Tests/StorageChronicle.UI.Headless.Tests.csproj'
)
foreach ($project in $projects) {
    $name = [IO.Path]::GetFileNameWithoutExtension($project)
    dotnet test (Join-Path $root $project) -c $Configuration --no-restore --results-directory $artifact --coverage --coverage-output (Join-Path $artifact "$name.cobertura.xml") --coverage-output-format cobertura
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
exit 0
