[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('SC-Test-W11-VBox', 'SC-Test-W10-VBox')][string]$VmName,
    [ValidateSet('Workload', 'Mft', 'NonNtfs', 'AclDenied')][string]$Role = 'Workload',
    [string]$TestId = ([guid]::NewGuid().ToString('N')),
    [int]$SizeGiB,
    [string]$ConfigPath,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Write-Error 'This legacy VirtualBox VDI creation/attachment helper is retired under Requirement 37. It performs no host, VM, disk, file, or evidence operations, regardless of -Apply. Whole-OS VM testing is out of scope. Use only a separately audited, user-approved dedicated test volume or file-backed VHDX workflow after all safety gates pass.'
exit 2
