param([switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$args = @('build', (Join-Path $root 'StorageChronicle.slnx'))
if ($NoRestore) { $args += '--no-restore' }
& dotnet @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
