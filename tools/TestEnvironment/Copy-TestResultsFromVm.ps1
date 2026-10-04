[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('SC-Test-W11-VBox', 'SC-Test-W10-VBox')][string]$VmName,
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$DestinationPath,
    [pscredential]$Credential,
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
Write-Error 'Retired under Requirement 37: collecting artifacts from a whole-OS guest is not part of acceptance. This entry point performs no host, VM, guest, file, credential, or evidence I/O.'
exit 2
