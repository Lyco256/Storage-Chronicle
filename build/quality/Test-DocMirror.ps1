$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifact = Join-Path $root 'artifacts/quality/docs'
New-Item -ItemType Directory -Force -Path $artifact | Out-Null
dotnet run --project (Join-Path $root 'tools/StorageChronicle.DocMirrorValidator/StorageChronicle.DocMirrorValidator.csproj') --no-restore -- $root *> (Join-Path $artifact 'doc-mirror.log')
exit $LASTEXITCODE
