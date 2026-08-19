[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('SC-Test-W11', 'SC-Test-W10')][string]$VmName,
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$DestinationPath,
    [pscredential]$Credential,
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestLab.Common.ps1')
$config = Get-TestLabConfig -ConfigPath $ConfigPath
$root = Assert-TestLabRoot -Root $config.Root
Assert-HyperVMutationPrerequisites
$null = Assert-ExactTestLabVm -Name $VmName
$destination = Assert-PathUnderRoot -Root ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\artifacts\acceptance\testlab'))) -Path $DestinationPath
$parent = Split-Path -Parent $destination
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$sessionParameters = @{ VMName = $VmName; ErrorAction = 'Stop' }
if ($null -ne $Credential) { $sessionParameters.Credential = $Credential }
$session = New-PSSession @sessionParameters
try { Copy-Item -FromSession $session -LiteralPath $SourcePath -Destination $destination -Force -ErrorAction Stop }
finally { Remove-PSSession $session -ErrorAction SilentlyContinue }
