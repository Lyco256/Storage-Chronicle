$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
dotnet run --project (Join-Path $root 'tools/StorageChronicle.DocMirrorValidator/StorageChronicle.DocMirrorValidator.csproj') --no-restore -- $root
exit $LASTEXITCODE
