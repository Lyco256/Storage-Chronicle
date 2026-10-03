[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('SC-Test-W11-VBox', 'SC-Test-W10-VBox')][string]$VmName,
    [Parameter(Mandatory = $true)][string]$VhdxPath,
    [Parameter(Mandatory = $true)][string]$TestId,
    [Parameter(Mandatory = $true)][ValidateSet('Workload', 'Mft', 'NonNtfs', 'AclDenied')][string]$Role,
    [string]$ConfigPath,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Write-Error 'This VirtualBox VDI cleanup helper is retired under Requirement 37. It performs no host, VM, disk, or file operations. Preserve existing test data; use only a separately audited run-owned file-backed VHDX workflow after every safety gate passes.'
exit 2
