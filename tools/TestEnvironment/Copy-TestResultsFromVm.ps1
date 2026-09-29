[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('SC-Test-W11-VBox', 'SC-Test-W10-VBox')][string]$VmName,
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$DestinationPath,
    [pscredential]$Credential,
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestLab.Common.ps1')
$config = Get-TestLabConfig -ConfigPath $ConfigPath
$root = Assert-TestLabRoot -Root $config.Root
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\artifacts\acceptance\testlab'))
$destination = Assert-PathUnderRoot -Root $artifactRoot -Path $DestinationPath
$vm = Assert-ExactTestLabVm -Name $VmName
if ([string]$vm.State -ne 'running') { throw "Approved VirtualBox VM is not running: $VmName" }
$guestCredential = Get-TestLabGuestCredential -Credential $Credential -CredentialReference ([string]$config.GuestCredentialReference)
$parent = Split-Path -Parent $destination
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$tempDirectory = Join-Path $root '.copy-results'
New-Item -ItemType Directory -Force -Path $tempDirectory | Out-Null
Copy-TestArtifactFromVm -VmName $VmName -Credential $guestCredential -GuestPath $SourcePath -HostDirectory $parent -TempRoot $tempDirectory
$copied = Join-Path $parent (Split-Path -Leaf $SourcePath)
if (-not [IO.Path]::GetFullPath($copied).Equals([IO.Path]::GetFullPath($destination), [StringComparison]::OrdinalIgnoreCase)) { Move-Item -LiteralPath $copied -Destination $destination -Force }
