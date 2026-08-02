$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
dotnet test (Join-Path $root 'tests/StorageChronicle.Architecture.Tests/StorageChronicle.Architecture.Tests.csproj') --no-restore
exit $LASTEXITCODE
