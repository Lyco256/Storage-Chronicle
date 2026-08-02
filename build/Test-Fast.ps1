param([switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$args = @('test', (Join-Path $root 'StorageChronicle.slnx'))
if ($NoRestore) { $args += '--no-restore' }
& dotnet @args
exit $LASTEXITCODE
