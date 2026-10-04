[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$RunId,
    [string]$VmName = 'SC-Test-W10-VBox'
)

$ErrorActionPreference = 'Stop'
Write-Error 'Retired under Requirement 37 and Requirement 26: guest-only Windows 10 Stage A checks cannot prove physical compatibility. This entry point performs no host, guest, API probe, file, or evidence I/O.'
exit 2
