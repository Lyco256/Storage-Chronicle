$ErrorActionPreference = 'Stop'
& "$PSScriptRoot\Build.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$PSScriptRoot\Test-Fast.ps1"
exit $LASTEXITCODE
