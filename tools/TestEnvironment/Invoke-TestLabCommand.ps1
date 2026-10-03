[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('SC-Test-W11-VBox', 'SC-Test-W10-VBox')][string]$VmName,
    [Parameter(Mandatory = $true)][string]$Command,
    [string]$ConfigPath,
    [pscredential]$Credential,
    [string]$CredentialReference
)

$ErrorActionPreference = 'Stop'
Write-Error 'Retired under Requirement 37: guest command execution is not part of physical acceptance. This entry point performs no host, VM, guest, disk, filesystem, credential, or evidence I/O. Use only the separately audited lightweight fixture workflow after all safety gates pass.'
exit 2
