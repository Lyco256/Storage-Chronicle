param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifact = Join-Path $root 'artifacts/quality/architecture'
New-Item -ItemType Directory -Force -Path $artifact | Out-Null
dotnet build (Join-Path $root 'StorageChronicle.slnx') --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$project = Join-Path $root 'tests/StorageChronicle.Architecture.Tests/StorageChronicle.Architecture.Tests.csproj'
$assembly = [IO.Path]::GetFileNameWithoutExtension($project)
$exe = Get-ChildItem (Join-Path $root 'tests/StorageChronicle.Architecture.Tests/bin') -Recurse -Filter "$assembly.exe" | Where-Object { $_.FullName -match "\\$Configuration\\" } | Select-Object -First 1
if ($null -eq $exe) { Write-Error "Test executable not found: $assembly.exe" }
& $exe.FullName -noLogo -automated sync -xml (Join-Path $artifact "$assembly.xml")
exit $LASTEXITCODE
