[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$AcceptanceRoot,
    [string]$VhdxPath,
    [string]$VhdxRoot,
    [string]$DevicePath,
    [string]$RemovableRoot,
    [string]$SmbShareName,
    [string]$ServiceName = 'StorageChronicleAgent',
    [switch]$CreateVhdx,
    [switch]$CreateUsnJournal,
    [switch]$WaitForMediaChange,
    [int]$MediaTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$arguments = @{
    Configuration = $Configuration
    AcceptanceRoot = $AcceptanceRoot
    VhdxPath = $VhdxPath
    VhdxRoot = $VhdxRoot
    DevicePath = $DevicePath
    RemovableRoot = $RemovableRoot
    SmbShareName = $SmbShareName
    ServiceName = $ServiceName
    CreateVhdx = $CreateVhdx
    CreateUsnJournal = $CreateUsnJournal
    WaitForMediaChange = $WaitForMediaChange
    MediaTimeoutSeconds = $MediaTimeoutSeconds
}
& (Join-Path $PSScriptRoot 'Test-Privileged.ps1') @arguments
exit $LASTEXITCODE
