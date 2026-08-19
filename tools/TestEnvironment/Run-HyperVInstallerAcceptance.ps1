[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Windows11', 'Windows10')][string]$Target,
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [Parameter(Mandatory = $true)][string]$UpdatedMsiPath,
    [Parameter(Mandatory = $true)][string]$RollbackMsiPath,
    [Parameter(Mandatory = $true)][string]$GuestCredentialReference,
    [Parameter(Mandatory = $true)][string]$NonAdminUser,
    [Parameter(Mandatory = $true)][string]$NonAdminCredentialReference,
    [Parameter(Mandatory = $true)][string]$SessionUser,
    [string]$ConfigPath,
    [string]$GuestTestDataRoot = 'D:\StorageChronicleTestData',
    [string]$OutputDirectory,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'TestLab.Common.ps1')

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repositoryRoot 'artifacts/acceptance/installer-vm' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$manifestPath = Join-Path $OutputDirectory ("hyperv-installer-$Target.json")
$manifest = $null
$exitCode = 1

try {
    $config = Get-TestLabConfig -ConfigPath $ConfigPath
    $root = Assert-TestLabRoot -Root $config.Root
    $definition = Get-TestLabVmDefinition -Guest $Target
    $iso = [string]$config[$definition.IsoKey]
    Assert-ExistingIso -Path $iso -Label "$Target ISO"
    foreach ($path in @($MsiPath, $UpdatedMsiPath, $RollbackMsiPath, $GuestCredentialReference)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required host input is missing: $path" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot '..\PhysicalAcceptance\Invoke-HyperVInstallerCase.ps1') -PathType Leaf)) { throw 'The Hyper-V installer driver is missing.' }

    $manifest = [ordered]@{
        Schema = 'StorageChronicle.HyperVInstallerAcceptance.v1'
        Target = $Target
        VmName = $definition.Name
        TargetOs = if ($Target -eq 'Windows10') { 'Windows10-22H2' } else { 'Windows11' }
        Apply = [bool]$Apply
        Status = if ($Apply) { 'RUNNING' } else { 'READY_FOR_USER_APPLY' }
        AcceptanceEligible = $false
        OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
        StartedUtc = [DateTimeOffset]::UtcNow
        Stages = @()
    }
    $manifestPath = Join-Path $OutputDirectory ("$($definition.Name)-installer-vm.json")
    if (-not $Apply) {
        $manifest.Stages += [ordered]@{ Name = 'preflight'; Status = 'READY_FOR_USER_APPLY'; Reason = 'All host inputs, approved VM identity, and ISO path were found; no VM or MSI operation was run.' }
        $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        Write-Output ($manifest | ConvertTo-Json -Depth 12)
        exit 2
    }

    Assert-HyperVMutationPrerequisites
    $vm = Assert-ExactTestLabVm -Name $definition.Name
    Assert-TestLabVmDisks -Vm $vm -Root $root
    & (Join-Path $PSScriptRoot 'Reset-TestVm.ps1') -Name $definition.Name -CheckpointName 'SC-CLEAN-BASELINE' -ConfigPath $ConfigPath -Apply | Out-File -LiteralPath (Join-Path $OutputDirectory 'reset.log') -Encoding UTF8
    if ($LASTEXITCODE -ne 0) { throw "Could not restore the clean installer baseline for $($definition.Name)." }
    Start-VM -Name $definition.Name -ErrorAction Stop | Out-Null
    $manifest.Stages += [ordered]@{ Name = 'baseline'; Status = 'PASSED'; Reason = 'Approved clean installer VM baseline restored and started.' }
    $installer = Join-Path $repositoryRoot 'build/package/Test-Installer.ps1'
    $driver = Join-Path $repositoryRoot 'tools/PhysicalAcceptance/Invoke-HyperVInstallerCase.ps1'
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $installer,
        '-MsiPath', ([IO.Path]::GetFullPath($MsiPath)), '-UpdatedMsiPath', ([IO.Path]::GetFullPath($UpdatedMsiPath)), '-RollbackMsiPath', ([IO.Path]::GetFullPath($RollbackMsiPath)),
        '-TargetOs', $manifest.TargetOs, '-TargetKind', 'HyperVVm', '-ExecutionMode', 'VM', '-DriverScript', $driver,
        '-VmName', $definition.Name, '-WindowsIsoPath', $iso, '-GuestCredentialReference', ([IO.Path]::GetFullPath($GuestCredentialReference)),
        '-GuestTestDataRoot', $GuestTestDataRoot, '-NonAdminUser', $NonAdminUser, '-NonAdminCredentialReference', $NonAdminCredentialReference,
        '-SessionUser', $SessionUser, '-HistoryPath', 'C:\ProgramData\Storage Chronicle\history', '-InstallPath', 'C:\Program Files\Storage Chronicle',
        '-StoragePermissionPath', 'C:\ProgramData\Storage Chronicle\history', '-OutputDirectory', $OutputDirectory, '-Execute'
    )
    & powershell.exe @arguments | Tee-Object -FilePath (Join-Path $OutputDirectory 'installer-harness.log')
    $installerExitCode = $LASTEXITCODE
    $manifest.Stages += [ordered]@{ Name = 'installer-matrix'; Status = if ($installerExitCode -eq 0) { 'PASSED' } else { 'FAILED' }; Reason = "Generic installer matrix exit code: $installerExitCode." }
    if ($installerExitCode -ne 0) { throw "Hyper-V installer matrix failed or was not executed: exit code $installerExitCode." }
    $manifest.Status = 'PASSED'
    $manifest.AcceptanceEligible = $true
    $exitCode = 0
}
catch {
    if ($null -eq $manifest) { $manifest = [ordered]@{ Schema = 'StorageChronicle.HyperVInstallerAcceptance.v1'; Target = $Target; AcceptanceEligible = $false; Status = 'FAILED'; Stages = @() } }
    $manifest.Status = 'FAILED'
    $manifest.AcceptanceEligible = $false
    $manifest.Error = $_.Exception.Message
    $exitCode = 1
}
finally {
    if ($Apply) {
        try {
            $definitionForCleanup = Get-TestLabVmDefinition -Guest $Target
            $vmForCleanup = Get-VM -Name $definitionForCleanup.Name -ErrorAction SilentlyContinue
            if ($null -ne $vmForCleanup -and [string]$vmForCleanup.State -ne 'Off') { Stop-VM -Name $definitionForCleanup.Name -TurnOff -Confirm:$false -ErrorAction Stop | Out-Null }
        } catch { if ($null -ne $manifest) { $manifest.CleanupError = $_.Exception.Message; $manifest.AcceptanceEligible = $false; $manifest.Status = 'FAILED'; $exitCode = 1 } }
    }
    if ($null -ne $manifest) {
        $manifest.CompletedUtc = [DateTimeOffset]::UtcNow
        $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        Write-Output ($manifest | ConvertTo-Json -Depth 12)
    }
}

exit $exitCode
