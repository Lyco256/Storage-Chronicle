[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Path)
$ErrorActionPreference = 'Stop'
try {
    $probe = Join-Path $Path ('probe-' + [guid]::NewGuid().ToString('N') + '.tmp')
    New-Item -ItemType File -LiteralPath $probe -Force | Out-Null
    Remove-Item -LiteralPath $probe -Force
    exit 1
} catch [UnauthorizedAccessException] { exit 0 }
catch { exit 0 }
