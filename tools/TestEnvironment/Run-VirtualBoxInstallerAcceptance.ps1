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
. (Join-Path $repositoryRoot 'build/quality/AcceptanceContracts.ps1')
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repositoryRoot 'artifacts/acceptance/installer-vm' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$definition = Get-TestLabVmDefinition -Guest $Target
$baselineSnapshot = 'SC-CLEAN-BASELINE'
$manifestPath = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "$($definition.Name)-installer-vbox.json"
$manifest = [ordered]@{ Schema = 'StorageChronicle.VirtualBoxInstallerAcceptance.v1'; Target = $Target; VmName = $definition.Name; TargetOs = $definition.TargetOs; TargetKind = 'VirtualBoxVm'; ExecutionMode = 'VM'; Apply = [bool]$Apply; Status = 'NOT_EXECUTED'; AcceptanceEligible = $false; OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory); StartedUtc = [DateTimeOffset]::UtcNow; Stages = @() }

try {
    $config = Get-TestLabConfig -ConfigPath $ConfigPath
    $root = Assert-TestLabRoot $config.Root
    Assert-ExistingIso -Path ([string]$config[$definition.IsoKey]) -Label "$Target ISO"
    foreach ($path in @($MsiPath, $UpdatedMsiPath, $RollbackMsiPath, $GuestCredentialReference)) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required host input is missing: $path" } }
    $vm = Assert-ExactTestLabVm $definition.Name
    Assert-TestLabVmDisks -Vm $vm -Root $root
    Assert-TestLabVmProfile -Name $definition.Name -Root $root | Out-Null
    Ensure-TestLabBaseline -Name $definition.Name -Snapshot $baselineSnapshot
    $resource = Assert-VirtualBoxResourceGate -Root $root
    $manifest.Resource = $resource
    if (-not $Apply) { $manifest.Status = 'READY_FOR_USER_APPLY'; $manifest.Stages += [ordered]@{ Name = 'preflight'; Status = 'READY_FOR_USER_APPLY'; Reason = 'VirtualBox VM, baseline, ISO, MSI, credential reference, and resource inputs are present; no installer operation was run. Re-run with -Apply only after explicit approval.' }; Write-TestLabJson $manifestPath $manifest; Write-Output ($manifest | ConvertTo-Json -Depth 12); exit 2 }
    Restore-TestLabBaseline -Name $definition.Name -Snapshot $baselineSnapshot
    Set-VBoxVmProvisioningSettings -Name $definition.Name -Provisioning:$false
    Start-TestLabVm -Name $definition.Name -Root $root
    $manifest.Stages += [ordered]@{ Name = 'baseline'; Status = 'PASSED'; Reason = 'Approved VirtualBox clean baseline restored and started with networking disconnected.' }
    $installer = Join-Path $repositoryRoot 'build/package/Test-Installer.ps1'
    $driver = Join-Path $repositoryRoot 'tools/PhysicalAcceptance/Invoke-VirtualBoxInstallerCase.ps1'
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $installer, '-MsiPath', ([IO.Path]::GetFullPath($MsiPath)), '-UpdatedMsiPath', ([IO.Path]::GetFullPath($UpdatedMsiPath)), '-RollbackMsiPath', ([IO.Path]::GetFullPath($RollbackMsiPath)), '-TargetOs', $definition.TargetOs, '-TargetKind', 'VirtualBoxVm', '-ExecutionMode', 'VM', '-DriverScript', $driver, '-VmName', $definition.Name, '-WindowsIsoPath', ([string]$config[$definition.IsoKey]), '-GuestCredentialReference', ([IO.Path]::GetFullPath($GuestCredentialReference)), '-GuestTestDataRoot', $GuestTestDataRoot, '-NonAdminUser', $NonAdminUser, '-NonAdminCredentialReference', $NonAdminCredentialReference, '-SessionUser', $SessionUser, '-HistoryPath', 'C:\ProgramData\Storage Chronicle\history', '-InstallPath', 'C:\Program Files\Storage Chronicle', '-StoragePermissionPath', 'C:\ProgramData\Storage Chronicle\history', '-OutputDirectory', $OutputDirectory, '-Execute')
    $previousConfigPath = $env:SC_TESTLAB_CONFIG
    try {
        if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) { $env:SC_TESTLAB_CONFIG = [IO.Path]::GetFullPath($ConfigPath) }
        & powershell.exe @arguments | Tee-Object -FilePath (Join-Path $OutputDirectory 'installer-harness-vbox.log')
        $installerExitCode = $LASTEXITCODE
    } finally {
        if ($null -eq $previousConfigPath) { Remove-Item Env:SC_TESTLAB_CONFIG -ErrorAction SilentlyContinue } else { $env:SC_TESTLAB_CONFIG = $previousConfigPath }
    }
    if ($installerExitCode -ne 0) { throw "VirtualBox installer matrix failed or was not executed: exit code $installerExitCode." }
    $installerManifests = @(Get-ChildItem -LiteralPath $OutputDirectory -Filter 'installer-acceptance-*.json' -File | Sort-Object LastWriteTimeUtc -Descending)
    if ($installerManifests.Count -eq 0) { throw 'The VirtualBox installer matrix did not produce a generic installer manifest.' }
    $installerManifestPath = $installerManifests[0].FullName
    $installerManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $installerManifestPath | ConvertFrom-Json
    if ([string]$installerManifest.Schema -ne 'storage-chronicle.installer-acceptance.v1' -or [string]$installerManifest.Status -ne 'PASSED' -or -not [bool]$installerManifest.AcceptanceEligible -or [string]$installerManifest.TargetKind -ne 'VirtualBoxVm' -or [string]$installerManifest.ExecutionMode -ne 'VM' -or [int]$installerManifest.Summary.Total -ne 11 -or [int]$installerManifest.Summary.Passed -ne 11 -or [int]$installerManifest.Summary.Failed -ne 0 -or [int]$installerManifest.Summary.NotExecuted -ne 0) { throw "The generic installer manifest is not an eligible eleven-case VirtualBox result: $installerManifestPath" }
    $requiredCaseIds = @(Get-RequiredInstallerCaseIds)
    $caseIds = @($installerManifest.Tests | ForEach-Object { [string]$_.CaseId })
    if (@($caseIds | Sort-Object -Unique).Count -ne $requiredCaseIds.Count -or @($requiredCaseIds | Where-Object { $caseIds -notcontains $_ }).Count -ne 0) { throw 'The VirtualBox installer manifest does not contain the defined eleven case IDs.' }
    $manifest.InstallerManifestPath = $installerManifestPath; $manifest.InstallerSummary = $installerManifest.Summary; $manifest.Stages += [ordered]@{ Name = 'installer-matrix'; Status = 'PASSED'; Reason = 'The real VirtualBox guest matrix produced one eligible generic manifest with all eleven cases passed.'; Evidence = $installerManifestPath }; $manifest.Status = 'PASSED'; $manifest.AcceptanceEligible = $true
}
catch { $manifest.Status = 'FAILED'; $manifest.Error = $_.Exception.Message }
finally {
    if ($Apply) { try { if ((Get-VBoxVmState $definition.Name) -ne 'poweroff') { Stop-TestLabVm $definition.Name }; Restore-TestLabBaseline -Name $definition.Name -Snapshot $baselineSnapshot } catch { $manifest.Status = 'FAILED'; $manifest.AcceptanceEligible = $false; $manifest.CleanupError = $_.Exception.Message } }
    $manifest.CompletedUtc = [DateTimeOffset]::UtcNow; Write-TestLabJson $manifestPath $manifest; Write-Output ($manifest | ConvertTo-Json -Depth 12)
}
if ($manifest.Status -eq 'PASSED' -and $manifest.AcceptanceEligible) { exit 0 }
if ($manifest.Status -eq 'READY_FOR_USER_APPLY') { exit 2 }
exit 1
