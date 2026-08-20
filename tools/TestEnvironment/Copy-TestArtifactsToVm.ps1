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
$vm = Assert-ExactTestLabVm -Name $VmName
if ([string]$vm.State -ne 'running') { throw "Approved VirtualBox VM is not running: $VmName" }
$guestCredential = Get-TestLabGuestCredential -Credential $Credential -CredentialReference ([string]$config.GuestCredentialReference)
Copy-TestArtifactToVm -VmName $VmName -Credential $guestCredential -SourcePath $SourcePath -GuestDirectory ([IO.Path]::GetDirectoryName($DestinationPath)) -TempRoot $root
if ([IO.Path]::GetFileName($SourcePath) -ne [IO.Path]::GetFileName($DestinationPath)) {
    $rename = "Move-Item -LiteralPath '$([IO.Path]::GetDirectoryName($DestinationPath).Replace("'", "''"))\$([IO.Path]::GetFileName($SourcePath).Replace("'", "''"))' -Destination '$($DestinationPath.Replace("'", "''"))' -Force"
    $result = Invoke-VBoxGuestControl -VmName $VmName -Credential $guestCredential -Executable 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -Arguments @('-NoProfile', '-NonInteractive', '-Command', $rename) -TempRoot $root
    if ($result.ExitCode -ne 0) { throw "Guest artifact rename failed: $($result.Error.Trim())" }
}
