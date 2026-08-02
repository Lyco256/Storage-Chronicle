$ErrorActionPreference = 'Stop'
& "$PSScriptRoot\Test-DocMirror.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$PSScriptRoot\Test-Architecture.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$PSScriptRoot\Test-Integration.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$PSScriptRoot\Test-Coverage.ps1"
exit $LASTEXITCODE
