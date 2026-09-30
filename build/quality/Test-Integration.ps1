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
    $projectDirectory = Split-Path -Parent $projectPath
    $assemblyName = [IO.Path]::GetFileNameWithoutExtension($projectPath)
    $testExecutable = Get-ChildItem (Join-Path $projectDirectory 'bin\Debug') -Recurse -File -Filter ($assemblyName + '.exe') |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $testExecutable) { Write-Error "Test executable was not found: $assemblyName"; exit 6 }
    & $testExecutable.FullName --progress off --minimum-expected-tests 1 --results-directory $artifact
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
exit 0
