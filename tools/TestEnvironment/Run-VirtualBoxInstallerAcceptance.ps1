[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Windows11', 'Windows10')][string]$Target,
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [Parameter(Mandatory = $true)][string]$UpdatedMsiPath,
    [Parameter(Mandatory = $true)][string]$RollbackMsiPath,
    [Parameter(Mandatory = $true)][string]$GuestCredentialReference,
    [Parameter(Mandatory = $true)][string]$NonAdminUser,
    [Parameter(Mandatory = $true)][string]$NonAdminCredentialReference,
    [Parameter(Mandatory = $true)][string]$SessionUser,
    [string]$ConfigPath,
    [string]$GuestTestDataRoot = 'D:\StorageChronicleTestData',
    [string]$OutputDirectory,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Write-Error 'Retired under Requirements 26 and 37: whole-OS VirtualBox installer acceptance is prohibited. This entry point performs no host, VM, snapshot, installer, service, file, or evidence I/O.'
exit 2
