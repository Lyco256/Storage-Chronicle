[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CaseId,
    [Parameter(Mandatory = $true)][ValidateSet('Windows10-22H2', 'Windows11')][string]$TargetOs,
    [ValidateSet('PhysicalMachine', 'VirtualBoxVm')][string]$TargetKind = 'VirtualBoxVm',
    [Parameter(Mandatory = $true)][ValidateSet('Local', 'VM')][string]$ExecutionMode,
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [string]$UpdatedMsiPath,
    [string]$RollbackMsiPath,
    [Parameter(Mandatory = $true)][string]$VmName,
    [string]$WindowsIsoPath,
    [string]$ServiceName = 'StorageChronicleAgent',
    [string]$NonAdminUser,
    [string]$NonAdminCredentialReference,
    [string]$SessionUser,
    [string]$ServiceCredentialReference,
    [Parameter(Mandatory = $true)][string]$GuestCredentialReference,
    [string]$GuestTestDataRoot = 'D:\StorageChronicleTestData',
    [string]$HistoryPath = 'C:\ProgramData\Storage Chronicle\history',
    [string]$InstallPath = 'C:\Program Files\Storage Chronicle',
    [string]$StoragePermissionPath = 'C:\ProgramData\Storage Chronicle\history',
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [Parameter(Mandatory = $true)][string]$LogPath
)

$ErrorActionPreference = 'Stop'
Write-Error 'Retired under Requirements 26 and 37: the VirtualBox installer case driver is prohibited. This entry point performs no host, VM, guest, installer, service, file, credential, or evidence I/O.'
exit 2
