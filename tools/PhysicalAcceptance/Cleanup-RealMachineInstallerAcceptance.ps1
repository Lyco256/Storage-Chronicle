[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$BundleRoot,
    [string]$TestDataRoot,
    [switch]$UninstallProduct,
    [switch]$RemoveAcceptanceVhdx,
    [switch]$ConfirmCleanup
)

$ErrorActionPreference = 'Stop'
if (-not $ConfirmCleanup) { throw 'Cleanup is destructive and requires -ConfirmCleanup.' }
$BundleRoot = [IO.Path]::GetFullPath($BundleRoot)
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Cleanup requires an elevated PowerShell.' }

if ($UninstallProduct) {
    $msi = Join-Path $BundleRoot 'StorageChronicle.msi'
    if (-not (Test-Path -LiteralPath $msi -PathType Leaf)) { throw "Bundle MSI is missing: $msi" }
    if ($PSCmdlet.ShouldProcess('Storage Chronicle product registration', 'Uninstall without removing ProgramData history')) {
        $process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -ArgumentList @('/x', $msi, '/qn', '/norestart') -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "msiexec uninstall failed with exit code $($process.ExitCode)." }
    }
}

if ($RemoveAcceptanceVhdx) {
    if ([string]::IsNullOrWhiteSpace($TestDataRoot)) { throw '-TestDataRoot is required with -RemoveAcceptanceVhdx.' }
    $root = [IO.Path]::GetFullPath($TestDataRoot).TrimEnd('\')
    if ($root -match '^[A-Za-z]:$' -or $root -match '^C:\?$' -or $root -in @($env:WINDIR.TrimEnd('\'), $env:ProgramFiles.TrimEnd('\'), $env:ProgramData.TrimEnd('\'))) { throw 'Refusing to remove from a system or volume root.' }
    $vhdxFiles = @(Get-ChildItem -LiteralPath $root -Filter '*.vhdx' -File -Recurse -ErrorAction SilentlyContinue | Where-Object { Test-Path -LiteralPath (Join-Path $_.DirectoryName '.storage-chronicle-testlab-marker.json') -PathType Leaf })
    foreach ($vhdx in $vhdxFiles) {
        if ($PSCmdlet.ShouldProcess($vhdx.FullName, 'Dismount and remove explicitly marked acceptance VHDX')) {
            $dismountCommand = Get-Command Dismount-DiskImage -ErrorAction SilentlyContinue
            if ($null -ne $dismountCommand) { & $dismountCommand.Name -ImagePath $vhdx.FullName -ErrorAction Stop }
            Remove-Item -LiteralPath $vhdx.FullName -Force
        }
    }
}

Write-Output 'Cleanup completed. ProgramData history was not removed.'
exit 0
