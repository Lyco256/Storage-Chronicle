[CmdletBinding()]
param([switch]$RunPrivileged)

$ErrorActionPreference = 'Stop'
& "$PSScriptRoot\Build.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$PSScriptRoot\Test-Fast.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$PSScriptRoot\quality\Test-Quality.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$PSScriptRoot\Test-Ui.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($RunPrivileged) {
    & "$PSScriptRoot\Test-WindowsPrivileged.ps1"
    exit $LASTEXITCODE
}

Write-Host 'Privileged Windows acceptance is isolated. Run build/Test-WindowsPrivileged.ps1 with its configured VHDX/media/SMB/service environment.' -ForegroundColor Yellow
exit 0
