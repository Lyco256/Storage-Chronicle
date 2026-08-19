[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CaseId,
    [Parameter(Mandatory = $true)][ValidateSet('Windows10-22H2', 'Windows11')][string]$TargetOs,
    [ValidateSet('PhysicalMachine', 'HyperVVm')][string]$TargetKind = 'HyperVVm',
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

$resultDirectory = Split-Path -Parent $ResultPath
$evidenceDirectory = Join-Path $resultDirectory 'evidence'
New-Item -ItemType Directory -Force -Path $resultDirectory, $evidenceDirectory | Out-Null
$result = [ordered]@{
    CaseId = $CaseId
    Status = 'FAILED'
    Reason = $null
    Target = [ordered]@{ OS = $TargetOs; Kind = $TargetKind; Mode = $ExecutionMode; Isolated = $false; IsAdministrator = $false; VmName = $VmName; TestDataRoot = $GuestTestDataRoot }
    Assertions = @()
}

function Write-Result {
    $result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
}

function Import-GuestCredential {
    if ([string]::IsNullOrWhiteSpace($GuestCredentialReference)) { throw 'A DPAPI-protected guest credential reference is required.' }
    if (-not (Test-Path -LiteralPath $GuestCredentialReference -PathType Leaf)) { throw "Guest credential reference does not exist: $GuestCredentialReference" }
    $credential = Import-Clixml -LiteralPath $GuestCredentialReference
    if ($credential -isnot [pscredential]) { throw 'The guest credential reference did not contain a PSCredential.' }
    return $credential
}

function Copy-ToGuest {
    param([Parameter(Mandatory = $true)]$Session, [Parameter(Mandatory = $true)][string]$Source, [Parameter(Mandatory = $true)][string]$Destination)
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Installer guest payload is missing: $Source" }
    Copy-Item -LiteralPath $Source -Destination $Destination -ToSession $Session -Force -ErrorAction Stop
}

function Get-GuestResultPayload {
    param([Parameter(Mandatory = $true)]$Session, [Parameter(Mandatory = $true)][string]$GuestResultPath, [Parameter(Mandatory = $true)][string]$GuestEvidenceDirectory)
    $guestResultCopy = Join-Path $resultDirectory 'guest-result.json'
    Copy-Item -FromSession $Session -LiteralPath $GuestResultPath -Destination $guestResultCopy -Force -ErrorAction Stop
    $payload = Get-Content -Raw -Encoding UTF8 -LiteralPath $guestResultCopy | ConvertFrom-Json
    if ([string]$payload.CaseId -ne $CaseId) { throw "Guest result CaseId '$($payload.CaseId)' does not match '$CaseId'." }
    foreach ($assertion in @($payload.Assertions)) {
        $guestEvidencePath = [string]$assertion.EvidencePath
        if ([string]::IsNullOrWhiteSpace($guestEvidencePath)) { continue }
        $leaf = Split-Path -Leaf $guestEvidencePath
        if ([string]::IsNullOrWhiteSpace($leaf)) { throw 'Guest assertion evidence did not have a file name.' }
        $hostEvidencePath = Join-Path $evidenceDirectory $leaf
        Copy-Item -FromSession $Session -LiteralPath (Join-Path $GuestEvidenceDirectory $leaf) -Destination $hostEvidencePath -Force -ErrorAction Stop
        $assertion.EvidencePath = $hostEvidencePath
    }
    $payload.Target.VmName = $VmName
    $payload.Target.Kind = 'HyperVVm'
    $payload.Target.Mode = 'VM'
    $payload.Target.TestDataRoot = $GuestTestDataRoot
    return $payload
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'The Hyper-V installer driver requires Windows.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'The Hyper-V installer driver requires an elevated host process.' }
    if ($TargetKind -ne 'HyperVVm' -or $ExecutionMode -ne 'VM') { throw 'The Hyper-V installer driver requires TargetKind=HyperVVm and ExecutionMode=VM.' }
    Assert-HyperVMutationPrerequisites
    $vm = Assert-ExactTestLabVm -Name $VmName
    if ([string]$vm.State -ne 'Running') { throw "The approved installer VM is not running: $VmName" }
    if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) { throw "Base MSI is missing: $MsiPath" }

    $guestCredential = Import-GuestCredential
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
    $session = New-PSSession -VMName $VmName -Credential $guestCredential -ErrorAction Stop
    try {
        Invoke-Command -Session $session -ScriptBlock {
            param($Root, $CaseRoot)
            New-Item -ItemType Directory -Force -Path $Root, $CaseRoot | Out-Null
        } -ArgumentList $guestRoot, $guestCaseRoot | Out-Null
        Copy-ToGuest -Session $session -Source (Join-Path $PSScriptRoot 'Invoke-RealInstallerCase.ps1') -Destination $guestScript
        Copy-ToGuest -Session $session -Source (Join-Path $PSScriptRoot 'Probe-WriteAccess.ps1') -Destination $guestProbe
        Copy-ToGuest -Session $session -Source $MsiPath -Destination $guestMsi
        if (-not [string]::IsNullOrWhiteSpace($UpdatedMsiPath)) { Copy-ToGuest -Session $session -Source $UpdatedMsiPath -Destination $guestUpdatedMsi }
        if (-not [string]::IsNullOrWhiteSpace($RollbackMsiPath)) { Copy-ToGuest -Session $session -Source $RollbackMsiPath -Destination $guestRollbackMsi }

        $guestInvocation = [pscustomobject]@{
            ScriptPath = $guestScript
            AcceptanceRoot = $GuestTestDataRoot
            Arguments = @(
                '-CaseId', $CaseId, '-TargetOs', $TargetOs, '-TargetKind', 'HyperVVm', '-ExecutionMode', 'VM',
                '-MsiPath', $guestMsi, '-UpdatedMsiPath', $guestUpdatedMsi, '-RollbackMsiPath', $guestRollbackMsi,
                '-VmName', $VmName, '-ServiceName', $ServiceName, '-NonAdminUser', $NonAdminUser,
                '-NonAdminCredentialReference', $NonAdminCredentialReference, '-SessionUser', $SessionUser,
                '-ServiceCredentialReference', $ServiceCredentialReference, '-GuestTestDataRoot', $GuestTestDataRoot,
                '-HistoryPath', $HistoryPath, '-InstallPath', $InstallPath, '-StoragePermissionPath', $StoragePermissionPath,
                '-ResultPath', $guestResult, '-LogPath', $guestLog
            )
        }
        $remoteOutput = @(Invoke-Command -Session $session -ScriptBlock {
            param($Invocation)
            $ErrorActionPreference = 'Stop'
            $env:STORAGE_CHRONICLE_ACCEPTANCE_TEST_ROOT = [string]$Invocation.AcceptanceRoot
            $arguments = [string[]]$Invocation.Arguments
            & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ([string]$Invocation.ScriptPath) @arguments
            [pscustomobject]@{ ExitCode = $LASTEXITCODE }
        } -ArgumentList $guestInvocation)
        $remoteResult = $remoteOutput | Where-Object { $null -ne $_.PSObject.Properties['ExitCode'] } | Select-Object -Last 1
        if ($null -eq $remoteResult) { throw 'The guest installer case did not return an exit code.' }
        $payload = Get-GuestResultPayload -Session $session -GuestResultPath $guestResult -GuestEvidenceDirectory $guestEvidence
        $result.Status = [string]$payload.Status
        $result.Reason = [string]$payload.Reason
        $result.Target = $payload.Target
        $result.Assertions = @($payload.Assertions)
        if ([int]$remoteResult.ExitCode -ne 0 -and $result.Status -eq 'PASSED') { throw "The guest installer case returned exit code $($remoteResult.ExitCode) despite a passing result." }
        $log = [ordered]@{ Guest = $VmName; CaseId = $CaseId; ExitCode = [int]$remoteResult.ExitCode; Output = @($remoteOutput | ForEach-Object { [string]$_ }); Result = $payload }
        $log | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $LogPath -Encoding UTF8
    }
    finally {
        if ($session -is [IDisposable]) { Remove-PSSession $session -ErrorAction SilentlyContinue }
    }
}
catch {
    $result.Status = 'FAILED'
    $result.Reason = $_.Exception.Message
    [ordered]@{ CaseId = $CaseId; Error = $_.Exception.Message } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $LogPath -Encoding UTF8
}

Write-Result
exit $(if ($result.Status -eq 'PASSED') { 0 } else { 1 })
