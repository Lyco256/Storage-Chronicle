[CmdletBinding()]
param(
    [string]$TestLabManifestPath,
    [string]$PrivilegedManifestPath,
    [string]$InstallerManifestPath,
    [string]$CloudFilesCheckPath,
    [string]$NoDriverCheckPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Write-Error 'Retired under Requirement 37 and Requirement 26: legacy Windows 10 Stage A/VM manifests cannot establish physical acceptance. This entry point performs no file or evidence I/O.'
exit 2
