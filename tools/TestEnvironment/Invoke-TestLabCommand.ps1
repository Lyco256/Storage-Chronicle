[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('SC-Test-W11', 'SC-Test-W10')][string]$VmName,
    [string]$Command,
    [string]$ScriptPath,
    [object[]]$ArgumentList = @(),
    [pscredential]$Credential,
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestLab.Common.ps1')
$config = Get-TestLabConfig -ConfigPath $ConfigPath
$null = Assert-TestLabRoot -Root $config.Root
Assert-HyperVMutationPrerequisites
$null = Assert-ExactTestLabVm -Name $VmName
if ([string]::IsNullOrWhiteSpace($Command) -and [string]::IsNullOrWhiteSpace($ScriptPath)) { throw 'Specify exactly one of -Command or -ScriptPath.' }
if (-not [string]::IsNullOrWhiteSpace($Command) -and -not [string]::IsNullOrWhiteSpace($ScriptPath)) { throw 'Specify exactly one of -Command or -ScriptPath.' }
if ($ScriptPath) {
    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) { throw "Guest script was not found: $ScriptPath" }
    $Command = Get-Content -Raw -LiteralPath $ScriptPath
}
$parameters = @{ VMName = $VmName; ScriptBlock = [scriptblock]::Create($Command); ArgumentList = $ArgumentList; ErrorAction = 'Stop' }
if ($null -ne $Credential) { $parameters.Credential = $Credential }
Invoke-Command @parameters
