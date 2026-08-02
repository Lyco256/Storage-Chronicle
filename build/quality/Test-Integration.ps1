$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projects = @(
    'tests/StorageChronicle.Integration.Tests/StorageChronicle.Integration.Tests.csproj',
    'tests/StorageChronicle.EndToEnd.Tests/StorageChronicle.EndToEnd.Tests.csproj',
    'tests/StorageChronicle.UI.Headless.Tests/StorageChronicle.UI.Headless.Tests.csproj',
    'tests/StorageChronicle.Installer.Tests/StorageChronicle.Installer.Tests.csproj'
)
foreach ($project in $projects) {
    dotnet test (Join-Path $root $project) --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
exit 0
