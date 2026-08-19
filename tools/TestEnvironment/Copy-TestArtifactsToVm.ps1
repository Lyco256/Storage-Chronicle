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
$null = Assert-TestLabRoot -Root $config.Root
Assert-HyperVMutationPrerequisites
$null = Assert-ExactTestLabVm -Name $VmName
if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) { throw "Artifact does not exist: $SourcePath" }
$sessionParameters = @{ VMName = $VmName; ErrorAction = 'Stop' }
if ($null -ne $Credential) { $sessionParameters.Credential = $Credential }
$session = New-PSSession @sessionParameters
try { Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -ToSession $session -Force -ErrorAction Stop }
finally { Remove-PSSession $session -ErrorAction SilentlyContinue }
