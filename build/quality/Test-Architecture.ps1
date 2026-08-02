param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifact = Join-Path $root 'artifacts/quality/architecture'
New-Item -ItemType Directory -Force -Path $artifact | Out-Null
$projects = @(
    'src/StorageChronicle.Domain/StorageChronicle.Domain.csproj',
    'src/StorageChronicle.Contracts/StorageChronicle.Contracts.csproj',
    'src/StorageChronicle.Application/StorageChronicle.Application.csproj',
    'src/StorageChronicle.Normalization/StorageChronicle.Normalization.csproj',
    'src/StorageChronicle.State/StorageChronicle.State.csproj',
    'src/StorageChronicle.Projection/StorageChronicle.Projection.csproj',
    'src/StorageChronicle.Storage/StorageChronicle.Storage.csproj',
    'src/StorageChronicle.Platform.Abstractions/StorageChronicle.Platform.Abstractions.csproj',
    'src/StorageChronicle.UI.Shared/StorageChronicle.UI.Shared.csproj',
    'tests/StorageChronicle.Architecture.Tests/StorageChronicle.Architecture.Tests.csproj'
)
foreach ($buildProject in $projects) {
    dotnet build (Join-Path $root $buildProject) -c $Configuration --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$project = Join-Path $root 'tests/StorageChronicle.Architecture.Tests/StorageChronicle.Architecture.Tests.csproj'
$assembly = [IO.Path]::GetFileNameWithoutExtension($project)
dotnet test $project -c $Configuration --no-build --results-directory $artifact
exit $LASTEXITCODE
