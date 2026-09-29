[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$BundleRoot,
    [string]$TestDataRoot,
    [switch]$UninstallProduct,
    [switch]$RemoveAcceptanceVhdx,
    [switch]$ConfirmCleanup
)

$ErrorActionPreference = 'Stop'
if ($UninstallProduct) { throw 'Automated product uninstall is disabled. Use the separately audited installer acceptance case on a dedicated physical test PC.' }
if ($RemoveAcceptanceVhdx) { throw 'Automated VHDX deletion is disabled. Test data and evidence are retained until an exact run-ownership cleanup workflow is implemented and reviewed.' }

Write-Output 'No cleanup was performed. Product state, test data, and evidence were left unchanged.'
exit 0
