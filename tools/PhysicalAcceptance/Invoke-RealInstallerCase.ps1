[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CaseId,
    [Parameter(Mandatory = $true)][ValidateSet('Windows10-22H2', 'Windows11')][string]$TargetOs,
    [ValidateSet('PhysicalMachine')][string]$TargetKind = 'PhysicalMachine',
    [Parameter(Mandatory = $true)][ValidateSet('Local')][string]$ExecutionMode,
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [string]$UpdatedMsiPath,
    [string]$RollbackMsiPath,
    [string]$VmName,
    [string]$WindowsIsoPath,
    [string]$ServiceName = 'StorageChronicleAgent',
    [string]$NonAdminUser,
    [string]$NonAdminCredentialReference,
    [string]$SessionUser,
    [string]$ServiceCredentialReference,
    [string]$GuestCredentialReference,
    [string]$GuestTestDataRoot,
    [string]$HistoryPath = 'C:\ProgramData\Storage Chronicle\history',
    [string]$InstallPath = 'C:\Program Files\Storage Chronicle',
    [string]$StoragePermissionPath = 'C:\ProgramData\Storage Chronicle\history',
    [Parameter(Mandatory = $true)][guid]$RunId,
    [Parameter(Mandatory = $true)][string]$ExpectedComputerName,
    [Parameter(Mandatory = $true)][string]$OwnerReceiptPath,
    [Parameter(Mandatory = $true)][string]$AuthorizationNonce,
    [Parameter(Mandatory = $true)][string]$CaseAuthorizationPhrase,
    [switch]$ConfirmDedicatedPhysicalMachine,
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [Parameter(Mandatory = $true)][string]$LogPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-NoReparsePath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($fullPath.StartsWith('\\', [StringComparison]::Ordinal)) { throw "UNC paths are not accepted: $fullPath" }
    $rootPath = [IO.Path]::GetPathRoot($fullPath)
    if ($rootPath -notmatch '^[A-Za-z]:\\$') { throw "A local path is required: $fullPath" }
    $cursor = $rootPath
    foreach ($segment in $fullPath.Substring($rootPath.Length).Split([char[]]@('\\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $segment
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point in physical installer path: $cursor" }
        }
    }
}

$resultDirectory = Split-Path -Parent $ResultPath
$evidenceDirectory = Join-Path $resultDirectory 'evidence'
if (-not (Test-Path -LiteralPath $resultDirectory -PathType Container)) { throw "The run-owned result directory must already exist: $resultDirectory" }
if (-not (Test-Path -LiteralPath $OwnerReceiptPath -PathType Leaf)) { throw 'The physical installer owner receipt is missing.' }
$receiptDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $OwnerReceiptPath)).TrimEnd('\')
$resultDirectoryFull = [IO.Path]::GetFullPath($resultDirectory).TrimEnd('\')
if (-not $resultDirectoryFull.StartsWith($receiptDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Case results must remain beneath the exact run-owned receipt directory.' }
Assert-NoReparsePath $receiptDirectory
Assert-NoReparsePath $resultDirectoryFull
if (Test-Path -LiteralPath $evidenceDirectory) { throw "Refusing to reuse an existing evidence directory: $evidenceDirectory" }
if (Test-Path -LiteralPath $ResultPath -or Test-Path -LiteralPath $LogPath) { throw 'ResultPath and LogPath must both be new paths.' }
New-Item -ItemType Directory -Path $evidenceDirectory | Out-Null

$result = [ordered]@{
    RunId = $RunId.ToString('D')
    CaseId = $CaseId
    Status = 'FAILED'
    Reason = $null
    Target = [ordered]@{
        OS = $TargetOs
        Kind = $TargetKind
        Mode = $ExecutionMode
        Isolated = $false
        IsAdministrator = $false
        VmName = $VmName
        TestDataRoot = $null
    }
    Assertions = @()
}

$assertions = [System.Collections.Generic.List[object]]::new()

function Write-Evidence {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][object]$Value
    )

    $safeName = ($Name -replace '[^A-Za-z0-9_.-]', '_')
    $path = Join-Path $evidenceDirectory "$safeName.json"
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 20))
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    return $path
}

function Add-Assertion {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$Passed,
        [Parameter(Mandatory = $true)][string]$Details,
        [Parameter(Mandatory = $true)][string]$EvidencePath
    )

    [void]$assertions.Add([ordered]@{
        Name = $Name
        Status = if ($Passed) { 'PASSED' } else { 'FAILED' }
        Details = $Details
        EvidencePath = $EvidencePath
    })
    if (-not $Passed) { throw "Assertion failed: $Name - $Details" }
}

function Invoke-Captured {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [int]$TimeoutSeconds = 300
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "Could not start $FilePath" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            $process.WaitForExit()
            return [pscustomobject]@{ ExitCode = 124; TimedOut = $true; Output = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; TimedOut = $false; Output = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}

function Invoke-Msi {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Install', 'Repair', 'Uninstall')][string]$Action,
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$EvidenceName
    )

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw "MSI does not exist: $PackagePath" }
    $arguments = if ($Action -eq 'Install') { @('/i', $PackagePath, '/qn', '/norestart', '/L*v', (Join-Path $evidenceDirectory "$EvidenceName-msiexec.log")) } elseif ($Action -eq 'Repair') { @('/famus', $PackagePath, '/qn', '/norestart', '/L*v', (Join-Path $evidenceDirectory "$EvidenceName-msiexec.log")) } else { @('/x', $PackagePath, '/qn', '/norestart', '/L*v', (Join-Path $evidenceDirectory "$EvidenceName-msiexec.log")) }
    $captured = Invoke-Captured -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -Arguments $arguments
    $evidence = Write-Evidence -Name $EvidenceName -Value ([ordered]@{ Action = $Action; PackagePath = $PackagePath; ExitCode = $captured.ExitCode; TimedOut = $captured.TimedOut; Output = $captured.Output; Error = $captured.Error })
    if ($captured.TimedOut -or $captured.ExitCode -ne 0) { throw "msiexec $Action failed with exit code $($captured.ExitCode)." }
    return $evidence
}

function Get-InstalledProduct {
    $locations = @(
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    return @(Get-ItemProperty -Path $locations -ErrorAction SilentlyContinue | Where-Object { [string]$_.DisplayName -eq 'Storage Chronicle' })
}

function Assert-RunOwnedProductPresent {
    $products = @(Get-InstalledProduct)
    if ($products.Count -ne 1) { throw "This case requires exactly one product installed during run $RunId; found $($products.Count)." }
    if (-not (Test-Path -LiteralPath $InstallPath -PathType Container)) { throw "The run-owned install directory is missing: $InstallPath" }
    $registeredInstallPath = [string]$products[0].InstallLocation
    if (-not [string]::IsNullOrWhiteSpace($registeredInstallPath) -and -not [IO.Path]::GetFullPath($registeredInstallPath).TrimEnd('\').Equals([IO.Path]::GetFullPath($InstallPath).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "The registered product points outside the run-owned install directory: $registeredInstallPath"
    }
    return $products[0]
}

function Assert-PreviousCasePassed([string]$PreviousCaseId) {
    $previousPath = Join-Path $resultDirectory "$PreviousCaseId.json"
    if (-not (Test-Path -LiteralPath $previousPath -PathType Leaf)) { throw "Required prior run case evidence is missing: $previousPath" }
    $previous = Get-Content -Raw -Encoding UTF8 -LiteralPath $previousPath | ConvertFrom-Json
    if ([string]$previous.RunId -ne $RunId.ToString('D') -or [string]$previous.CaseId -ne $PreviousCaseId -or [string]$previous.Status -ne 'PASSED') { throw "Required prior case did not pass in this exact installer run: $PreviousCaseId" }
}

function Assert-RunOwnedService {
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    if ($null -eq $service) { throw "The run-owned service is missing: $ServiceName" }
    $servicePath = [string]$service.PathName
    if ([string]::IsNullOrWhiteSpace($servicePath) -or $servicePath.IndexOf([IO.Path]::GetFullPath($InstallPath), [StringComparison]::OrdinalIgnoreCase) -lt 0) { throw "Service executable path is not under the run-owned install root: $servicePath" }
    return $service
}

function Stop-ExactStartedProcess([Diagnostics.Process]$Process, [string]$ExpectedPath, [DateTime]$ExpectedStartTime) {
    $current = $null
    try {
        $current = [Diagnostics.Process]::GetProcessById($Process.Id)
        $currentPath = [IO.Path]::GetFullPath($current.MainModule.FileName)
        $expectedFullPath = [IO.Path]::GetFullPath($ExpectedPath)
        if ($current.StartTime.ToUniversalTime() -ne $ExpectedStartTime.ToUniversalTime() -or -not $currentPath.Equals($expectedFullPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to stop a process whose start time or executable path no longer matches this run.'
        }
        if (-not $current.HasExited) {
            if (-not $current.CloseMainWindow()) { $current.Kill() }
            if (-not $current.WaitForExit(5000) -and -not $current.HasExited) { $current.Kill() }
            $current.WaitForExit()
        }
    } finally { if ($null -ne $current) { $current.Dispose() } }
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-InstalledService {
    param([switch]$RequireRunning)
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    $evidence = Write-Evidence -Name 'service-state' -Value $service
    Add-Assertion -Name 'Agent service is installed as automatic LocalSystem service' -Passed ($null -ne $service -and [string]$service.StartMode -eq 'Auto' -and [string]$service.StartName -eq 'LocalSystem') -Details (if ($null -eq $service) { 'Service was not found.' } else { "StartMode=$($service.StartMode); StartName=$($service.StartName); State=$($service.State)" }) -EvidencePath $evidence
    $recovery = Invoke-Captured -FilePath (Join-Path $env:WINDIR 'System32\sc.exe') -Arguments @('qfailure', $ServiceName)
    $recoveryEvidence = Write-Evidence -Name 'service-recovery' -Value ([ordered]@{ ExitCode = $recovery.ExitCode; Output = $recovery.Output; Error = $recovery.Error })
    $hasFiveSecondDelay = $recovery.Output -match '(?<!\d)5000(?!\d)'
    $hasFifteenSecondDelay = $recovery.Output -match '(?<!\d)15000(?!\d)'
    $hasSixtySecondDelay = $recovery.Output -match '(?<!\d)60000(?!\d)'
    $recoveryPassed = $recovery.ExitCode -eq 0 -and $recovery.Output -match 'FAILURE_ACTIONS' -and $hasFiveSecondDelay -and $hasFifteenSecondDelay -and $hasSixtySecondDelay
    Add-Assertion -Name 'Service recovery policy is exactly 5s/15s/60s' -Passed $recoveryPassed -Details ("sc.exe qfailure: 5000ms={0}; 15000ms={1}; 60000ms={2}." -f $hasFiveSecondDelay, $hasFifteenSecondDelay, $hasSixtySecondDelay) -EvidencePath $recoveryEvidence
    if ($RequireRunning) {
        $runningEvidence = Write-Evidence -Name 'service-running-after-update' -Value ([ordered]@{ State = if ($null -eq $service) { $null } else { [string]$service.State } })
        Add-Assertion -Name 'Agent service restarts after update replacement' -Passed ($null -ne $service -and [string]$service.State -eq 'Running') -Details (if ($null -eq $service) { 'Service was not found after update.' } else { "State=$($service.State)" }) -EvidencePath $runningEvidence
    }
}

function Assert-InstalledFiles {
    $files = @(
        (Join-Path $InstallPath 'StorageChronicle.Agent.exe'),
        (Join-Path $InstallPath 'StorageChronicle.SessionAgent.exe'),
        (Join-Path $InstallPath 'StorageChronicle.UI.Desktop.exe')
    )
    $states = @($files | ForEach-Object { [ordered]@{ Path = $_; Exists = Test-Path -LiteralPath $_ -PathType Leaf } })
    $evidence = Write-Evidence -Name 'installed-files' -Value $states
    Add-Assertion -Name 'Self-contained product files are installed' -Passed (@($states | Where-Object { -not $_.Exists }).Count -eq 0) -Details 'All three product executables exist under the default Program Files directory.' -EvidencePath $evidence
}

function Assert-ProductEntry {
    $products = Get-InstalledProduct
    $evidence = Write-Evidence -Name 'product-registration' -Value $products
    Add-Assertion -Name 'Exactly one product registration exists' -Passed ($products.Count -eq 1) -Details "Found $($products.Count) Storage Chronicle product registrations." -EvidencePath $evidence
}

function Assert-DataDirectory {
    $historyRoot = [IO.Path]::GetFullPath($HistoryPath)
    $dataRoot = [IO.Path]::GetFullPath((Split-Path -Parent $historyRoot))
    $states = @($dataRoot, $historyRoot) | ForEach-Object {
        $exists = Test-Path -LiteralPath $_ -PathType Container
        $acl = if ($exists) { Get-Acl -LiteralPath $_ } else { $null }
        $identities = if ($null -eq $acl) { @() } else { @($acl.Access | ForEach-Object { [string]$_.IdentityReference }) }
        [ordered]@{ Path = $_; Exists = $exists; HasSystem = $identities -contains 'NT AUTHORITY\SYSTEM'; HasAdministrators = @($identities | Where-Object { $_ -match 'BUILTIN\\Administrators$' }).Count -gt 0 }
    }
    $evidence = Write-Evidence -Name 'data-directory-acl' -Value $states
    Add-Assertion -Name 'ProgramData history and ACL roots are created' -Passed (@($states | Where-Object { -not $_.Exists -or -not $_.HasSystem -or -not $_.HasAdministrators }).Count -eq 0) -Details 'The default ProgramData data/history roots exist and expose SYSTEM and Administrators ACL entries.' -EvidencePath $evidence
}

function Assert-NoHistoryDeletionOption {
    $products = Get-InstalledProduct
    $uninstallStrings = @($products | ForEach-Object { [string]$_.UninstallString })
    $hasDeletionOption = @($uninstallStrings | Where-Object { $_ -match '(?i)(REMOVE|DELETE|PURGE).*HISTORY|HISTORY.*(REMOVE|DELETE|PURGE)' }).Count -gt 0
    $evidence = Write-Evidence -Name 'history-deletion-option' -Value ([ordered]@{ UninstallStrings = $uninstallStrings; HasHistoryDeletionOption = $hasDeletionOption })
    Add-Assertion -Name 'Uninstall exposes no history deletion option' -Passed (-not $hasDeletionOption) -Details 'The registered uninstall command does not provide a history purge/delete option.' -EvidencePath $evidence
}

function Load-Credential {
    if ([string]::IsNullOrWhiteSpace($NonAdminCredentialReference)) { throw 'A CLIXML credential reference is required for the non-admin acceptance cases.' }
    if (-not (Test-Path -LiteralPath $NonAdminCredentialReference -PathType Leaf)) { throw "Credential reference does not exist: $NonAdminCredentialReference" }
    return Import-Clixml -LiteralPath $NonAdminCredentialReference
}

function Invoke-NonAdminProcess {
    param([Parameter(Mandatory = $true)][string]$FilePath, [string[]]$Arguments = @())
    $credential = Load-Credential
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Credential $credential -WorkingDirectory (Split-Path -Parent $FilePath) -PassThru
    $processStartTime = $process.StartTime
    try {
        Start-Sleep -Seconds 5
        return [ordered]@{ ProcessId = $process.Id; HasExited = $process.HasExited; ExitCode = if ($process.HasExited) { $process.ExitCode } else { $null }; User = $credential.UserName }
    } finally { if (-not $process.HasExited) { Stop-ExactStartedProcess $process $FilePath $processStartTime } }
}

function Assert-SessionAgent {
    $path = Join-Path $InstallPath 'StorageChronicle.SessionAgent.exe'
    $runKeyPath = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
    $runProperties = Get-ItemProperty -LiteralPath $runKeyPath -Name 'StorageChronicleSessionAgent' -ErrorAction SilentlyContinue
    $registeredPath = if ($null -eq $runProperties) { $null } else { [string]$runProperties.StorageChronicleSessionAgent }
    $registrationEvidence = Write-Evidence -Name 'session-agent-logon-registration' -Value ([ordered]@{ RegistryPath = $runKeyPath; ValueName = 'StorageChronicleSessionAgent'; RegisteredPath = $registeredPath; ExpectedPath = $path })
    Add-Assertion -Name 'Session Agent is registered for user logon' -Passed ([string]::Equals($registeredPath, $path, [StringComparison]::OrdinalIgnoreCase)) -Details "RegisteredPath=$registeredPath; ExpectedPath=$path." -EvidencePath $registrationEvidence
    $process = Start-Process -FilePath $path -ArgumentList @('--pipe-name', 'StorageChronicle.Agent') -PassThru
    $processStartTime = $process.StartTime
    try {
        Start-Sleep -Seconds 3
        $details = [ordered]@{ ProcessId = $process.Id; RunningAfterStartupWindow = -not $process.HasExited; SessionId = $process.SessionId; User = [Environment]::UserName }
        $evidence = Write-Evidence -Name 'session-agent-startup' -Value $details
        Add-Assertion -Name 'Session Agent starts in the interactive session' -Passed ($details.RunningAfterStartupWindow -and $details.SessionId -eq ([Diagnostics.Process]::GetCurrentProcess().SessionId)) -Details "SessionId=$($details.SessionId); current session=$([Diagnostics.Process]::GetCurrentProcess().SessionId)." -EvidencePath $evidence
    } finally { if (-not $process.HasExited) { Stop-ExactStartedProcess $process $path $processStartTime } }
}

function Assert-StoragePermission {
    $permissionRoot = Join-Path $StoragePermissionPath ('acceptance-permission-' + [guid]::NewGuid().ToString('N'))
    $historyFullPath = [IO.Path]::GetFullPath($HistoryPath).TrimEnd('\')
    $permissionFullPath = [IO.Path]::GetFullPath($permissionRoot)
    if ((Split-Path -Parent $permissionFullPath).TrimEnd('\') -ine $historyFullPath) { throw 'The ACL fixture must be a new direct child of this run-owned history directory.' }
    if (-not (Test-Path -LiteralPath $historyFullPath -PathType Container) -or (Test-Path -LiteralPath $permissionFullPath)) { throw 'The run-owned history root must exist and the unique ACL fixture path must be new.' }
    $historyVolume = Get-Volume -FilePath $historyFullPath -ErrorAction Stop
    if ([string]$historyVolume.FileSystem -ne 'NTFS' -or [string]$historyVolume.DriveType -ne 'Fixed') { throw 'The ACL fixture requires the run-owned local NTFS history volume.' }
    New-Item -ItemType Directory -Path $permissionFullPath | Out-Null
    $acl = Get-Acl -LiteralPath $permissionRoot
    $account = [Security.Principal.NTAccount]::new($NonAdminUser)
    $rule = [Security.AccessControl.FileSystemAccessRule]::new($account, [Security.AccessControl.FileSystemRights]::Write, [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Deny)
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $permissionRoot -AclObject $acl
    try {
        $probe = Join-Path $PSScriptRoot 'Probe-WriteAccess.ps1'
        $probeResult = Invoke-NonAdminProcess -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $probe, '-Path', $permissionRoot)
        $evidence = Write-Evidence -Name 'storage-permission' -Value ([ordered]@{ Path = $permissionRoot; Probe = $probeResult; NonAdminUser = $NonAdminUser })
        Add-Assertion -Name 'Non-admin write is denied on the protected acceptance path' -Passed ($probeResult.HasExited -and $probeResult.ExitCode -eq 0) -Details "ProbeExitCode=$($probeResult.ExitCode); 0 means UnauthorizedAccessException was observed." -EvidencePath $evidence
    } finally {
        $acl = Get-Acl -LiteralPath $permissionRoot
        $acl.RemoveAccessRule($rule) | Out-Null
        Set-Acl -LiteralPath $permissionRoot -AclObject $acl
        $entries = @(Get-ChildItem -LiteralPath $permissionRoot -Force -ErrorAction Stop)
        if ($entries.Count -ne 0) { throw 'The run-owned ACL fixture is not empty; preserve it for review instead of recursively deleting it.' }
        [IO.Directory]::Delete($permissionFullPath, $false)
    }
}

try {
    $result.Target.IsAdministrator = Test-Administrator
    if (-not $result.Target.IsAdministrator) { throw 'The installer driver requires an elevated process.' }
    if (-not $ConfirmDedicatedPhysicalMachine -or -not $ExpectedComputerName.Equals($env:COMPUTERNAME, [StringComparison]::OrdinalIgnoreCase)) { throw 'The driver requires explicit authorization bound to the current physical test PC.' }
    if ([string]::IsNullOrWhiteSpace($AuthorizationNonce) -or -not $AuthorizationNonce.Equals([Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_INSTALLER_AUTHORIZATION_NONCE', 'Process'), [StringComparison]::Ordinal)) { throw 'The run-scoped installer authorization capability is missing or does not match.' }
    $expectedCaseAuthorization = "I AUTHORIZE STORAGE CHRONICLE CASE $CaseId ON $env:COMPUTERNAME RUN $RunId"
    if (-not $CaseAuthorizationPhrase.Equals($expectedCaseAuthorization, [StringComparison]::Ordinal)) { throw 'The case authorization phrase does not bind this exact case, physical PC, and run.' }
    Write-Host "This driver will execute installer case '$CaseId' on physical PC '$env:COMPUTERNAME' for run '$RunId'. It may change Windows Installer, Program Files, ProgramData history, service, and registry state. Type the exact case authorization phrase to continue: $expectedCaseAuthorization"
    $typedCaseAuthorization = [Console]::ReadLine()
    if ($null -eq $typedCaseAuthorization -or -not $typedCaseAuthorization.Equals($expectedCaseAuthorization, [StringComparison]::Ordinal)) { throw 'The driver did not receive the exact interactive case authorization; no installer case was executed.' }
    if (-not (Test-Path -LiteralPath $OwnerReceiptPath -PathType Leaf)) { throw 'The physical installer owner receipt is missing.' }
    $ownerReceipt = Get-Content -Raw -Encoding UTF8 -LiteralPath $OwnerReceiptPath | ConvertFrom-Json
    if ([string]$ownerReceipt.Schema -ne 'StorageChronicle.PhysicalInstallerOwnerReceipt.v1' -or [string]$ownerReceipt.RunId -ne $RunId.ToString('D') -or [string]$ownerReceipt.ComputerName -ne $env:COMPUTERNAME -or [string]$ownerReceipt.HumanConfirmation -cne "I CONFIRM DEDICATED PC $env:COMPUTERNAME RUN $RunId" -or [string]$ownerReceipt.EvidenceRoot -ine $receiptDirectory -or [string]$ownerReceipt.InstallPath -ine [IO.Path]::GetFullPath($InstallPath) -or [string]$ownerReceipt.HistoryPath -ine [IO.Path]::GetFullPath($HistoryPath) -or [string]$ownerReceipt.StoragePermissionPath -ine [IO.Path]::GetFullPath($StoragePermissionPath)) { throw 'The installer owner receipt does not match this run, PC, human confirmation, evidence, install, history, or permission path.' }
    $canonicalInstallPath = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Storage Chronicle')).TrimEnd('\')
    $canonicalHistoryPath = [IO.Path]::GetFullPath((Join-Path (Join-Path $env:ProgramData 'Storage Chronicle') 'history')).TrimEnd('\')
    if (-not [IO.Path]::GetFullPath($InstallPath).TrimEnd('\').Equals($canonicalInstallPath, [StringComparison]::OrdinalIgnoreCase) -or -not [IO.Path]::GetFullPath($HistoryPath).TrimEnd('\').Equals($canonicalHistoryPath, [StringComparison]::OrdinalIgnoreCase) -or -not [IO.Path]::GetFullPath($StoragePermissionPath).TrimEnd('\').Equals($canonicalHistoryPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer driver paths must be the canonical product install/history paths.' }
    if ($receiptDirectory -match '(?i)\\OneDrive\\|\\Documents\\' -or $receiptDirectory -match '^\\\\' -or $receiptDirectory.StartsWith([IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Owner receipt evidence cannot be under the repository, a synchronized folder, or UNC path.' }
    $receiptVolume = Get-Volume -FilePath $receiptDirectory -ErrorAction Stop
    if ([string]$receiptVolume.FileSystem -ne 'NTFS' -or [string]$receiptVolume.DriveType -ne 'Fixed') { throw 'The owner receipt evidence path must be on a local fixed NTFS volume.' }
    $isPhysicalLocal = $TargetKind -eq 'PhysicalMachine' -and $ExecutionMode -eq 'Local'
    if (-not $isPhysicalLocal) { throw 'This installer case driver supports only an explicitly authorized physical machine.' }
    $acceptanceRoot = [Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_ACCEPTANCE_TEST_ROOT', 'Process')
    if ([string]::IsNullOrWhiteSpace($acceptanceRoot) -or -not (Test-Path -LiteralPath $acceptanceRoot -PathType Container)) { throw 'The driver was not given an existing approved acceptance root.' }
    $acceptanceRoot = [IO.Path]::GetFullPath($acceptanceRoot).TrimEnd('\\')
    $blockedRoots = @('C:', 'C:\\', $env:WINDIR, $env:ProgramFiles, $env:ProgramData) | ForEach-Object { try { [IO.Path]::GetFullPath($_).TrimEnd('\\') } catch { $_ } }
    if ($acceptanceRoot -match '^[A-Za-z]:$' -or $acceptanceRoot -in $blockedRoots) { throw "The driver refused a system or volume acceptance root: $acceptanceRoot" }
    $repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).TrimEnd('\\')
    $bundleRoot = [IO.Path]::GetFullPath((Split-Path -Parent $MsiPath)).TrimEnd('\\')
    $protectedFixtureRoots = @($env:WINDIR, $env:ProgramFiles, $env:ProgramData) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\\') }
    $insideProtectedFixtureRoot = @($protectedFixtureRoots | Where-Object { $acceptanceRoot.Equals($_, [StringComparison]::OrdinalIgnoreCase) -or $acceptanceRoot.StartsWith($_ + '\', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    if ($acceptanceRoot -match '(?i)\\OneDrive\\|\\Documents\\' -or $acceptanceRoot.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or $acceptanceRoot.StartsWith($repositoryRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or $acceptanceRoot.Equals($bundleRoot, [StringComparison]::OrdinalIgnoreCase) -or $acceptanceRoot.StartsWith($bundleRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or $insideProtectedFixtureRoot) { throw 'The driver refused a fixture root inside a system/application root, repository, bundle, or synchronized user folder.' }
    $marker = Join-Path $acceptanceRoot '.storage-chronicle-testlab-marker.json'
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw "The acceptance root lacks the required marker: $marker" }
    $volumeMarker = Join-Path $acceptanceRoot 'StorageChronicleTestVolume.json'
    if (-not (Test-Path -LiteralPath $volumeMarker -PathType Leaf)) { throw "The acceptance root lacks the required volume marker: $volumeMarker" }
    $markerValue = Get-Content -Raw -Encoding UTF8 -LiteralPath $marker | ConvertFrom-Json
    $volumeMarkerValue = Get-Content -Raw -Encoding UTF8 -LiteralPath $volumeMarker | ConvertFrom-Json
    if ([string]$markerValue.Schema -ne 'StorageChronicle.TestLabDataMarker.v1' -or [string]$markerValue.TestId -ne $RunId.ToString('D') -or [string]$markerValue.Role -ne 'Workload' -or [string]$markerValue.VolumeLabel -ne 'SC_TEST_VOLUME' -or [string]$markerValue.FileSystem -ne 'NTFS' -or [string]$volumeMarkerValue.Schema -ne 'StorageChronicle.TestLabDataMarker.v1' -or [string]$volumeMarkerValue.TestId -ne $RunId.ToString('D') -or [string]$volumeMarkerValue.Role -ne 'Workload' -or [string]$volumeMarkerValue.VolumeLabel -ne 'SC_TEST_VOLUME' -or [string]$volumeMarkerValue.FileSystem -ne 'NTFS') { throw 'The acceptance root markers do not match this exact installer run and workload volume.' }
    $acceptanceVolume = Get-Volume -FilePath $acceptanceRoot -ErrorAction Stop
    if ([string]$ownerReceipt.TestDataRoot -ine $acceptanceRoot -or [string]$ownerReceipt.MarkerTestId -ne $RunId.ToString('D') -or [string]$acceptanceVolume.FileSystem -ne 'NTFS' -or [string]$acceptanceVolume.DriveType -ne 'Fixed' -or [string]$acceptanceVolume.UniqueId -ne [string]$ownerReceipt.TestDataVolumeUniqueId -or [string]$acceptanceVolume.UniqueId -ne [string]$markerValue.VolumeUniqueId -or [string]$volumeMarkerValue.VolumeUniqueId -ne [string]$markerValue.VolumeUniqueId) { throw 'The acceptance root path, run ID, NTFS fixed-volume role, and volume identity do not match the owner receipt and markers.' }
    if (-not (Get-FileHash -Algorithm SHA256 -LiteralPath $MsiPath).Hash.Equals([string]$ownerReceipt.BaseMsiSha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'Base MSI hash no longer matches the approved run receipt.' }
    if ($UpdatedMsiPath -and -not (Get-FileHash -Algorithm SHA256 -LiteralPath $UpdatedMsiPath).Hash.Equals([string]$ownerReceipt.UpdatedMsiSha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'Updated MSI hash no longer matches the approved run receipt.' }
    if ($RollbackMsiPath -and -not (Get-FileHash -Algorithm SHA256 -LiteralPath $RollbackMsiPath).Hash.Equals([string]$ownerReceipt.RollbackMsiSha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'Rollback MSI hash no longer matches the approved run receipt.' }
    $result.Target.Isolated = $true
    $result.Target.TestDataRoot = $acceptanceRoot
    if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) { throw "Base MSI is missing: $MsiPath" }
    switch ($CaseId) {
        'clean-install' {
            $dataRoot = Join-Path $env:ProgramData 'Storage Chronicle'
            if ((Get-InstalledProduct).Count -ne 0 -or $null -ne (Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $InstallPath) -or (Test-Path -LiteralPath $dataRoot)) { throw 'Clean install refuses any pre-existing product, service, install path, or ProgramData data root.' }
            Invoke-Msi -Action Install -PackagePath $MsiPath -EvidenceName $CaseId | Out-Null
            Assert-InstalledFiles
            Assert-InstalledService
            Assert-ProductEntry
            Assert-DataDirectory
        }
        'repair' {
            Assert-RunOwnedProductPresent | Out-Null
            Invoke-Msi -Action Repair -PackagePath $MsiPath -EvidenceName $CaseId | Out-Null
            Assert-InstalledFiles
            Assert-InstalledService
        }
        'update' {
            Assert-RunOwnedProductPresent | Out-Null
            Assert-RunOwnedService | Out-Null
            $beforeProduct = @(Get-InstalledProduct | Select-Object -First 1)
            if ($beforeProduct.Count -ne 1) { throw "Expected one product registration before update, found $($beforeProduct.Count)." }
            $beforeVersion = [version][string]$beforeProduct[0].DisplayVersion
            $stopResult = Invoke-Captured -FilePath (Join-Path $env:WINDIR 'System32\sc.exe') -Arguments @('stop', $ServiceName)
            $stoppedService = $null
            for ($attempt = 0; $attempt -lt 30; $attempt++) {
                $stoppedService = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
                if ($null -ne $stoppedService -and [string]$stoppedService.State -eq 'Stopped') { break }
                Start-Sleep -Seconds 1
            }
            $stopEvidence = Write-Evidence -Name 'update-safe-stop' -Value ([ordered]@{ StopExitCode = $stopResult.ExitCode; StopOutput = $stopResult.Output; StopError = $stopResult.Error; StateAfterStopRequest = if ($null -eq $stoppedService) { $null } else { [string]$stoppedService.State } })
            Add-Assertion -Name 'Agent is safely stopped before update replacement' -Passed ($null -ne $stoppedService -and [string]$stoppedService.State -eq 'Stopped' -and $stopResult.ExitCode -in @(0, 1062)) -Details "StopExitCode=$($stopResult.ExitCode); StateAfterStopRequest=$($stoppedService.State)." -EvidencePath $stopEvidence
            Invoke-Msi -Action Install -PackagePath $UpdatedMsiPath -EvidenceName $CaseId | Out-Null
            Assert-InstalledFiles
            $afterProduct = @(Get-InstalledProduct | Select-Object -First 1)
            $afterVersion = if ($afterProduct.Count -eq 1) { [version][string]$afterProduct[0].DisplayVersion } else { $null }
            $versionEvidence = Write-Evidence -Name 'same-major-update' -Value ([ordered]@{ Before = [string]$beforeVersion; After = if ($null -eq $afterVersion) { $null } else { [string]$afterVersion } })
            Add-Assertion -Name 'Update remains within the same major product version' -Passed ($null -ne $afterVersion -and $afterVersion.Major -eq $beforeVersion.Major) -Details "Before=$beforeVersion; After=$afterVersion." -EvidencePath $versionEvidence
            Assert-InstalledService -RequireRunning
            Assert-ProductEntry
        }
        'rollback' {
            Assert-RunOwnedProductPresent | Out-Null
            Assert-RunOwnedService | Out-Null
            $intentionallyMissingUpdate = Join-Path $evidenceDirectory 'intentionally-failed-update.msi'
            $failedUpdate = Invoke-Captured -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -Arguments @('/i', $intentionallyMissingUpdate, '/qn', '/norestart', '/L*v', (Join-Path $evidenceDirectory "$CaseId-failed-update-msiexec.log"))
            $remainingProductCount = (Get-InstalledProduct).Count
            $failedUpdateEvidence = Write-Evidence -Name 'rollback-failed-update' -Value ([ordered]@{ AttemptedPackagePath = $intentionallyMissingUpdate; ExitCode = $failedUpdate.ExitCode; TimedOut = $failedUpdate.TimedOut; Output = $failedUpdate.Output; Error = $failedUpdate.Error; ProductCountAfterFailure = $remainingProductCount })
            Add-Assertion -Name 'Intentionally failed update is rejected without losing the installed product' -Passed ($failedUpdate.ExitCode -ne 0 -and -not $failedUpdate.TimedOut -and $remainingProductCount -eq 1) -Details "FailedUpdateExitCode=$($failedUpdate.ExitCode); ProductCountAfterFailure=$remainingProductCount." -EvidencePath $failedUpdateEvidence
            Invoke-Msi -Action Install -PackagePath $RollbackMsiPath -EvidenceName $CaseId | Out-Null
            Assert-InstalledFiles
            Assert-InstalledService
            Assert-ProductEntry
        }
        'uninstall' {
            Assert-RunOwnedProductPresent | Out-Null
            Assert-RunOwnedService | Out-Null
            if (-not (Test-Path -LiteralPath $HistoryPath -PathType Container)) { throw 'The clean-install run did not create its own history root; refusing to fabricate a product-data path.' }
            $retentionMarker = Join-Path $HistoryPath ('acceptance-uninstall-retention-' + [guid]::NewGuid().ToString('N') + '.marker')
            New-Item -ItemType File -Path $retentionMarker | Out-Null
            Assert-NoHistoryDeletionOption
            Invoke-Msi -Action Uninstall -PackagePath $MsiPath -EvidenceName $CaseId | Out-Null
            $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
            $retentionMarkerPresent = Test-Path -LiteralPath $retentionMarker -PathType Leaf
            $evidence = Write-Evidence -Name 'uninstall-state' -Value ([ordered]@{ ServicePresent = $null -ne $service; HistoryPresent = Test-Path -LiteralPath $HistoryPath -PathType Container; RetentionMarkerPresent = $retentionMarkerPresent; RetentionMarker = $retentionMarker; ProductCount = (Get-InstalledProduct).Count })
            Add-Assertion -Name 'Uninstall removes product registration and service but retains run-created history data' -Passed ($null -eq $service -and (Get-InstalledProduct).Count -eq 0 -and (Test-Path -LiteralPath $HistoryPath -PathType Container) -and $retentionMarkerPresent) -Details 'The service/product were removed while the new history marker remained.' -EvidencePath $evidence
        }
        'failed-install-rollback' {
            if ((Get-InstalledProduct).Count -ne 0) { throw 'Failed-install rollback expects the preceding run-owned uninstall to have removed the product.' }
            $missingInstall = Join-Path $evidenceDirectory 'intentionally-failed-install.msi'
            $failedInstall = Invoke-Captured -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -Arguments @('/i', $missingInstall, '/qn', '/norestart', '/L*v', (Join-Path $evidenceDirectory "$CaseId-msiexec.log"))
            $products = @(Get-InstalledProduct)
            $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
            $installPresent = Test-Path -LiteralPath $InstallPath -PathType Container
            $evidence = Write-Evidence -Name 'failed-install-rollback' -Value ([ordered]@{ AttemptedPackagePath = $missingInstall; ExitCode = $failedInstall.ExitCode; TimedOut = $failedInstall.TimedOut; ProductCount = $products.Count; ServicePresent = $null -ne $service; InstallPathPresent = $installPresent; Output = $failedInstall.Output; Error = $failedInstall.Error })
            Add-Assertion -Name 'Intentionally failed install leaves no half-registered product or service' -Passed ($failedInstall.ExitCode -ne 0 -and -not $failedInstall.TimedOut -and $products.Count -eq 0 -and $null -eq $service -and -not $installPresent) -Details "FailedInstallExitCode=$($failedInstall.ExitCode); ProductCount=$($products.Count); ServicePresent=$($null -ne $service); InstallPathPresent=$installPresent." -EvidencePath $evidence
        }
        'history-retention' {
            Assert-PreviousCasePassed 'uninstall'
            if ((Get-InstalledProduct).Count -ne 0) { throw 'History-retention expects the preceding run-owned uninstall to have removed the product.' }
            if (-not (Test-Path -LiteralPath $HistoryPath -PathType Container)) { throw 'The run-created history root was removed by uninstall; do not recreate it.' }
            $marker = Join-Path $HistoryPath ('acceptance-retention-' + [guid]::NewGuid().ToString('N') + '.marker')
            New-Item -ItemType File -Path $marker | Out-Null
            Invoke-Msi -Action Install -PackagePath $MsiPath -EvidenceName ($CaseId + '-install') | Out-Null
            Invoke-Msi -Action Uninstall -PackagePath $MsiPath -EvidenceName $CaseId | Out-Null
            $evidence = Write-Evidence -Name 'history-retention' -Value ([ordered]@{ MarkerPath = $marker; MarkerPresentAfterUninstall = Test-Path -LiteralPath $marker -PathType Leaf; HistoryRootPresent = Test-Path -LiteralPath $HistoryPath -PathType Container })
            Add-Assertion -Name 'History marker survives uninstall' -Passed ((Test-Path -LiteralPath $marker -PathType Leaf) -and (Test-Path -LiteralPath $HistoryPath -PathType Container)) -Details 'Only an empty acceptance marker was created; existing history was not deleted.' -EvidencePath $evidence
            Invoke-Msi -Action Install -PackagePath $MsiPath -EvidenceName ($CaseId + '-reinstall') | Out-Null
            $reuseEvidence = Write-Evidence -Name 'history-reuse-after-reinstall' -Value ([ordered]@{ MarkerPath = $marker; MarkerPresentAfterReinstall = Test-Path -LiteralPath $marker -PathType Leaf; ProductCount = (Get-InstalledProduct).Count })
            Add-Assertion -Name 'Reinstall can reuse retained history' -Passed ((Test-Path -LiteralPath $marker -PathType Leaf) -and (Get-InstalledProduct).Count -eq 1) -Details 'The retained history marker remains available after reinstall.' -EvidencePath $reuseEvidence
        }
        'service' {
            Assert-RunOwnedProductPresent | Out-Null
            Assert-InstalledService
        }
        'session' {
            Assert-RunOwnedProductPresent | Out-Null
            Assert-SessionAgent
        }
        'non-admin' {
            Assert-RunOwnedProductPresent | Out-Null
            $details = Invoke-NonAdminProcess -FilePath (Join-Path $InstallPath 'StorageChronicle.UI.Desktop.exe')
            $evidence = Write-Evidence -Name 'non-admin-ui' -Value $details
            Add-Assertion -Name 'UI launches as the declared non-admin user' -Passed (-not $details.HasExited) -Details "User=$($details.User); process=$($details.ProcessId) remained alive during the startup window." -EvidencePath $evidence
        }
        'storage-permission' {
            Assert-RunOwnedProductPresent | Out-Null
            Assert-StoragePermission
        }
        default { throw "Unknown installer acceptance case: $CaseId" }
    }
    $result.Status = 'PASSED'
    $result.Reason = 'Real installer case completed with host-visible evidence.'
} catch {
    $result.Status = 'FAILED'
    $result.Reason = $_.Exception.Message
}

$result.Assertions = @($assertions)
$resultJson = $result | ConvertTo-Json -Depth 20
$resultBytes = [Text.UTF8Encoding]::new($false).GetBytes($resultJson)
$resultStream = [IO.File]::Open($ResultPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $resultStream.Write($resultBytes, 0, $resultBytes.Length); $resultStream.Flush($true) } finally { $resultStream.Dispose() }
exit $(if ($result.Status -eq 'PASSED') { 0 } else { 1 })
