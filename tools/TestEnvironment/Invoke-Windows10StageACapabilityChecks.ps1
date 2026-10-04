[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$RunId,
    [string]$OutputDirectory,
    [pscredential]$Credential,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Write-Error 'Retired under Requirement 37 and Requirement 26: Windows 10 Stage A VirtualBox checks are not acceptance evidence. This entry point performs no VM, guest, host, disk, file, credential, or evidence I/O. Use only the physical Windows 10 bundle after all safety gates pass.'
exit 2
