param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifact = Join-Path $root 'artifacts/quality/coverage'
New-Item -ItemType Directory -Force -Path $artifact | Out-Null
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
