[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('SC-Test-W11-VBox', 'SC-Test-W10-VBox')][string]$VmName,
    [Parameter(Mandatory = $true)][string]$Command,
    [string]$ConfigPath,
    [pscredential]$Credential,
    [string]$CredentialReference
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'TestLab.Common.ps1')
try {
    $config = Get-TestLabConfig -ConfigPath $ConfigPath
    $root = Assert-TestLabRoot $config.Root
    $vm = Assert-ExactTestLabVm $VmName
    if ([string]$vm.State -ne 'running') { throw "Approved VirtualBox VM is not running: $VmName" }
    $reference = if (-not [string]::IsNullOrWhiteSpace($CredentialReference)) { $CredentialReference } else { [string]$config.GuestCredentialReference }
    $guestCredential = Get-TestLabGuestCredential -Credential $Credential -CredentialReference $reference
    $result = Invoke-VBoxGuestControl -VmName $VmName -Credential $guestCredential -Executable 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -Arguments @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', $Command) -TempRoot $root
    if ($result.ExitCode -ne 0) { throw "VirtualBox guestcontrol PowerShell command failed with exit code $($result.ExitCode): $($result.Error.Trim())" }
    Write-Output $result.Output
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 2
}
