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
    $assembly = Get-ChildItem (Join-Path $projectDirectory 'bin\Debug') -Recurse -File -Filter ($assemblyName + '.dll') |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $assembly) { Write-Error "Test assembly was not found: $assemblyName"; exit 6 }
    dotnet test $assembly.FullName.Substring($root.Length + 1) --no-restore --results-directory $artifact
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
exit 0
