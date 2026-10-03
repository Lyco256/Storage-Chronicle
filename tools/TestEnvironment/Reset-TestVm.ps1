[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][ValidateSet('SC-Test-W11-VBox', 'SC-Test-W10-VBox')][string]$Name,
    [string]$SnapshotName = 'SC-CLEAN-BASELINE',
    [string]$ConfigPath,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Write-Error 'Retired under Requirement 37: whole-OS VM snapshot restoration is not used. This entry point performs no host, VM, snapshot, disk, filesystem, or evidence I/O, regardless of -Apply or -WhatIf.'
exit 2
