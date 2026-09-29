$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifact = Join-Path $root 'artifacts/quality/integration'
New-Item -ItemType Directory -Force -Path $artifact | Out-Null
$projects = @(
    'tests/StorageChronicle.Integration.Tests/StorageChronicle.Integration.Tests.csproj',
    'tests/StorageChronicle.EndToEnd.Tests/StorageChronicle.EndToEnd.Tests.csproj',
    'tests/StorageChronicle.UI.Headless.Tests/StorageChronicle.UI.Headless.Tests.csproj'
)
foreach ($project in $projects) {
    $projectPath = Join-Path $root $project
    $assembly = [IO.Path]::GetFileNameWithoutExtension($projectPath)
    dotnet test $projectPath --no-build --results-directory $artifact
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
exit 0
