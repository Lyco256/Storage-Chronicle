param([string]$Filter = '*StorageChronicleBenchmarks*')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifact = Join-Path $root 'artifacts/benchmarks'
New-Item -ItemType Directory -Force -Path $artifact | Out-Null
dotnet run --project (Join-Path $root 'benchmarks/StorageChronicle.Benchmarks/StorageChronicle.Benchmarks.csproj') -c Release --no-restore -- --filter $Filter --artifacts $artifact --exporters json markdown
exit $LASTEXITCODE
