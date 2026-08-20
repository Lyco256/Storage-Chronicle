[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CaseId,
    [Parameter(Mandatory = $true)][ValidateSet('Windows10-22H2', 'Windows11')][string]$TargetOs,
    [ValidateSet('PhysicalMachine', 'VirtualBoxVm')][string]$TargetKind = 'VirtualBoxVm',
    [Parameter(Mandatory = $true)][ValidateSet('Local', 'VM')][string]$ExecutionMode,
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [string]$UpdatedMsiPath,
    [string]$RollbackMsiPath,
    [Parameter(Mandatory = $true)][string]$VmName,
    [string]$WindowsIsoPath,
    [string]$ServiceName = 'StorageChronicleAgent',
    [string]$NonAdminUser,
    [string]$NonAdminCredentialReference,
    [string]$SessionUser,
    [string]$ServiceCredentialReference,
    [Parameter(Mandatory = $true)][string]$GuestCredentialReference,
    [string]$GuestTestDataRoot = 'D:\StorageChronicleTestData',
    [string]$HistoryPath = 'C:\ProgramData\Storage Chronicle\history',
    [string]$InstallPath = 'C:\Program Files\Storage Chronicle',
    [string]$StoragePermissionPath = 'C:\ProgramData\Storage Chronicle\history',
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [Parameter(Mandatory = $true)][string]$LogPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\TestEnvironment\TestLab.Common.ps1')
$config = Get-TestLabConfig
$root = Assert-TestLabRoot $config.Root
$resultDirectory = Split-Path -Parent $ResultPath
$evidenceDirectory = Join-Path $resultDirectory 'evidence'
New-Item -ItemType Directory -Force -Path $resultDirectory, $evidenceDirectory | Out-Null
$result = [ordered]@{ CaseId = $CaseId; Status = 'FAILED'; Reason = $null; Target = [ordered]@{ OS = $TargetOs; Kind = $TargetKind; Mode = $ExecutionMode; Isolated = $false; IsAdministrator = $false; VmName = $VmName; TestDataRoot = $GuestTestDataRoot }; Assertions = @() }

function Write-Result { $result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ResultPath -Encoding UTF8 }
function Import-GuestCredential { Get-TestLabGuestCredential -CredentialReference $GuestCredentialReference }
function Invoke-GuestPowerShell([pscredential]$Credential, [string[]]$Arguments) {
    return Invoke-VBoxGuestControl -VmName $VmName -Credential $Credential -Executable 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -Arguments (@('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass') + $Arguments) -TempRoot $root
}
function Copy-ToGuest([pscredential]$Credential, [string]$Source, [string]$Destination) {
    $parent = Split-Path -Parent $Destination
    $command = "New-Item -ItemType Directory -Force -Path '$($parent.Replace("'", "''"))' | Out-Null"
    $directoryResult = Invoke-GuestPowerShell $Credential @('-Command', $command)
    if ($directoryResult.ExitCode -ne 0) { throw "Guest directory creation failed: $($directoryResult.Error.Trim())" }
    Copy-TestArtifactToVm -VmName $VmName -Credential $Credential -SourcePath $Source -GuestDirectory $parent -TempRoot $root
    $sourceLeaf = Split-Path -Leaf $Source
    if ($sourceLeaf -ne (Split-Path -Leaf $Destination)) {
        $rename = "Move-Item -LiteralPath '$($parent.Replace("'", "''"))\$($sourceLeaf.Replace("'", "''"))' -Destination '$($Destination.Replace("'", "''"))' -Force"
        $renameResult = Invoke-GuestPowerShell $Credential @('-Command', $rename)
        if ($renameResult.ExitCode -ne 0) { throw "Guest artifact rename failed: $($renameResult.Error.Trim())" }
    }
}
function Get-GuestResult([pscredential]$Credential, [string]$GuestResultPath, [string]$GuestEvidenceDirectory) {
    $guestResultCopy = Join-Path $resultDirectory 'guest-result.json'
    Copy-TestArtifactFromVm -VmName $VmName -Credential $Credential -GuestPath $GuestResultPath -HostDirectory $resultDirectory -TempRoot $root
    $copied = Join-Path $resultDirectory (Split-Path -Leaf $GuestResultPath)
    if (-not [IO.Path]::GetFullPath($copied).Equals([IO.Path]::GetFullPath($guestResultCopy), [StringComparison]::OrdinalIgnoreCase)) { Move-Item -LiteralPath $copied -Destination $guestResultCopy -Force }
    $payload = Get-Content -Raw -Encoding UTF8 -LiteralPath $guestResultCopy | ConvertFrom-Json
    if ([string]$payload.CaseId -ne $CaseId) { throw "Guest result CaseId '$($payload.CaseId)' does not match '$CaseId'." }
    foreach ($assertion in @($payload.Assertions)) {
        $guestEvidencePath = [string]$assertion.EvidencePath
        if ([string]::IsNullOrWhiteSpace($guestEvidencePath)) { continue }
        $leaf = Split-Path -Leaf $guestEvidencePath
        if ([string]::IsNullOrWhiteSpace($leaf) -or $leaf -ne [IO.Path]::GetFileName($leaf)) { throw 'Guest evidence path is invalid.' }
        $hostEvidencePath = Join-Path $evidenceDirectory $leaf
        Copy-TestArtifactFromVm -VmName $VmName -Credential $Credential -GuestPath (Join-Path $GuestEvidenceDirectory $leaf) -HostDirectory $evidenceDirectory -TempRoot $root
        $copiedEvidence = Join-Path $evidenceDirectory $leaf
        if (-not [IO.Path]::GetFullPath($copiedEvidence).Equals([IO.Path]::GetFullPath($hostEvidencePath), [StringComparison]::OrdinalIgnoreCase)) { Move-Item -LiteralPath $copiedEvidence -Destination $hostEvidencePath -Force }
        $assertion.EvidencePath = $hostEvidencePath
    }
    $payload.Target.VmName = $VmName
    $payload.Target.Kind = 'VirtualBoxVm'
    $payload.Target.Mode = 'VM'
    $payload.Target.TestDataRoot = $GuestTestDataRoot
    return $payload
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'The VirtualBox installer driver requires a Windows host.' }
    if ($TargetKind -ne 'VirtualBoxVm' -or $ExecutionMode -ne 'VM') { throw 'The VirtualBox installer driver requires VirtualBoxVm/VM.' }
    $vm = Assert-ExactTestLabVm $VmName
    Assert-TestLabVmDisks -Vm $vm -Root $root
    Assert-VBoxSafeSettings $VmName
    $credential = Import-GuestCredential
    Ensure-TestLabBaseline -Name $VmName -Snapshot 'SC-CLEAN-BASELINE'
    if ((Get-VBoxVmState $VmName) -ne 'poweroff') { Stop-TestLabVm -Name $VmName }
    Restore-TestLabBaseline -Name $VmName -Snapshot 'SC-CLEAN-BASELINE'
    Set-VBoxVmProvisioningSettings -Name $VmName -Provisioning:$false
    Start-TestLabVm -Name $VmName
    Wait-TestLabGuestReady -VmName $VmName -Credential $credential -TempRoot $root
    $guestIdentity = Invoke-VBoxGuestControl -VmName $VmName -Credential $credential -Executable 'C:\Windows\System32\whoami.exe' -TempRoot $root
    if ($guestIdentity.ExitCode -ne 0) { throw "VirtualBox guest whoami smoke check failed: $($guestIdentity.Error.Trim())" }
    foreach ($path in @($MsiPath, $UpdatedMsiPath, $RollbackMsiPath)) { if (-not [string]::IsNullOrWhiteSpace($path) -and -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Installer payload is missing: $path" } }
    $guestRoot = 'C:\StorageChronicleAcceptance\Installer'
    $guestCaseRoot = Join-Path $guestRoot $CaseId
    $guestScript = Join-Path $guestRoot 'Invoke-RealInstallerCase.ps1'
    $guestProbe = Join-Path $guestRoot 'Probe-WriteAccess.ps1'
    $guestMsi = Join-Path $guestRoot 'StorageChronicle.msi'
    $guestUpdatedMsi = Join-Path $guestRoot 'StorageChronicle.updated.msi'
    $guestRollbackMsi = Join-Path $guestRoot 'StorageChronicle.rollback.msi'
    $guestResult = Join-Path $guestCaseRoot 'result.json'
    $guestLog = Join-Path $guestCaseRoot 'case.log'
    $guestEvidence = Join-Path $guestCaseRoot 'evidence'
    $setup = "New-Item -ItemType Directory -Force -Path '$($guestCaseRoot.Replace("'", "''"))' | Out-Null"
    $setupResult = Invoke-GuestPowerShell $credential @('-Command', $setup)
    if ($setupResult.ExitCode -ne 0) { throw "Guest installer directory setup failed: $($setupResult.Error.Trim())" }
    Copy-ToGuest $credential (Join-Path $PSScriptRoot 'Invoke-RealInstallerCase.ps1') $guestScript
    Copy-ToGuest $credential (Join-Path $PSScriptRoot 'Probe-WriteAccess.ps1') $guestProbe
    Copy-ToGuest $credential $MsiPath $guestMsi
    if (-not [string]::IsNullOrWhiteSpace($UpdatedMsiPath)) { Copy-ToGuest $credential $UpdatedMsiPath $guestUpdatedMsi }
    if (-not [string]::IsNullOrWhiteSpace($RollbackMsiPath)) { Copy-ToGuest $credential $RollbackMsiPath $guestRollbackMsi }
    $arguments = @('-File', $guestScript, '-CaseId', $CaseId, '-TargetOs', $TargetOs, '-TargetKind', 'VirtualBoxVm', '-ExecutionMode', 'VM', '-MsiPath', $guestMsi, '-UpdatedMsiPath', $guestUpdatedMsi, '-RollbackMsiPath', $guestRollbackMsi, '-VmName', $VmName, '-ServiceName', $ServiceName, '-NonAdminUser', $NonAdminUser, '-NonAdminCredentialReference', $NonAdminCredentialReference, '-SessionUser', $SessionUser, '-ServiceCredentialReference', $ServiceCredentialReference, '-GuestTestDataRoot', $GuestTestDataRoot, '-HistoryPath', $HistoryPath, '-InstallPath', $InstallPath, '-StoragePermissionPath', $StoragePermissionPath, '-ResultPath', $guestResult, '-LogPath', $guestLog)
    $remote = Invoke-GuestPowerShell $credential $arguments
    $payload = Get-GuestResult -Credential $credential -GuestResultPath $guestResult -GuestEvidenceDirectory $guestEvidence
    $result.Status = [string]$payload.Status; $result.Reason = [string]$payload.Reason; $result.Target = $payload.Target; $result.Assertions = @($payload.Assertions)
    [ordered]@{ Guest = $VmName; CaseId = $CaseId; ExitCode = $remote.ExitCode; Output = $remote.Output; Error = $remote.Error; WhoAmI = $guestIdentity.Output; Result = $payload } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $LogPath -Encoding UTF8
}
catch { $result.Status = 'FAILED'; $result.Reason = $_.Exception.Message; [ordered]@{ CaseId = $CaseId; Error = $_.Exception.Message } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $LogPath -Encoding UTF8 }

Write-Result
exit $(if ($result.Status -eq 'PASSED') { 0 } else { 1 })
