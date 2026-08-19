[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CaseId,
    [Parameter(Mandatory = $true)][ValidateSet('Windows10-22H2', 'Windows11')][string]$TargetOs,
    [Parameter(Mandatory = $true)][ValidateSet('Local', 'VM')][string]$ExecutionMode,
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
    [string]$HistoryPath = 'C:\ProgramData\Storage Chronicle\history',
    [string]$InstallPath = 'C:\Program Files\Storage Chronicle',
    [string]$StoragePermissionPath = 'C:\ProgramData\Storage Chronicle\history',
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [Parameter(Mandatory = $true)][string]$LogPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resultDirectory = Split-Path -Parent $ResultPath
$evidenceDirectory = Join-Path $resultDirectory 'evidence'
New-Item -ItemType Directory -Force -Path $resultDirectory, $evidenceDirectory | Out-Null

$result = [ordered]@{
    CaseId = $CaseId
    Status = 'FAILED'
    Reason = $null
    Target = [ordered]@{
        OS = $TargetOs
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
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path -Encoding UTF8
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
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    return @(Get-ItemProperty -Path $locations -ErrorAction SilentlyContinue | Where-Object { [string]$_.DisplayName -eq 'Storage Chronicle' })
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-InstalledService {
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

function Load-Credential {
    if ([string]::IsNullOrWhiteSpace($NonAdminCredentialReference)) { throw 'A CLIXML credential reference is required for the non-admin acceptance cases.' }
    if (-not (Test-Path -LiteralPath $NonAdminCredentialReference -PathType Leaf)) { throw "Credential reference does not exist: $NonAdminCredentialReference" }
    return Import-Clixml -LiteralPath $NonAdminCredentialReference
}

function Invoke-NonAdminProcess {
    param([Parameter(Mandatory = $true)][string]$FilePath, [string[]]$Arguments = @())
    $credential = Load-Credential
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Credential $credential -WorkingDirectory (Split-Path -Parent $FilePath) -PassThru
    try {
        Start-Sleep -Seconds 5
        return [ordered]@{ ProcessId = $process.Id; HasExited = $process.HasExited; ExitCode = if ($process.HasExited) { $process.ExitCode } else { $null }; User = $credential.UserName }
    } finally {
        if (-not $process.HasExited) { try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { } }
    }
}

function Assert-SessionAgent {
    $path = Join-Path $InstallPath 'StorageChronicle.SessionAgent.exe'
    $runKeyPath = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
    $runProperties = Get-ItemProperty -LiteralPath $runKeyPath -Name 'StorageChronicleSessionAgent' -ErrorAction SilentlyContinue
    $registeredPath = if ($null -eq $runProperties) { $null } else { [string]$runProperties.StorageChronicleSessionAgent }
    $registrationEvidence = Write-Evidence -Name 'session-agent-logon-registration' -Value ([ordered]@{ RegistryPath = $runKeyPath; ValueName = 'StorageChronicleSessionAgent'; RegisteredPath = $registeredPath; ExpectedPath = $path })
    Add-Assertion -Name 'Session Agent is registered for user logon' -Passed ([string]::Equals($registeredPath, $path, [StringComparison]::OrdinalIgnoreCase)) -Details "RegisteredPath=$registeredPath; ExpectedPath=$path." -EvidencePath $registrationEvidence
    $process = Start-Process -FilePath $path -ArgumentList @('--pipe-name', 'StorageChronicle.Agent') -PassThru
    try {
        Start-Sleep -Seconds 3
        $details = [ordered]@{ ProcessId = $process.Id; RunningAfterStartupWindow = -not $process.HasExited; SessionId = $process.SessionId; User = [Environment]::UserName }
        $evidence = Write-Evidence -Name 'session-agent-startup' -Value $details
        Add-Assertion -Name 'Session Agent starts in the interactive session' -Passed ($details.RunningAfterStartupWindow -and $details.SessionId -eq ([Diagnostics.Process]::GetCurrentProcess().SessionId)) -Details "SessionId=$($details.SessionId); current session=$([Diagnostics.Process]::GetCurrentProcess().SessionId)." -EvidencePath $evidence
    } finally { if (-not $process.HasExited) { try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { } } }
}

function Assert-StoragePermission {
    $permissionRoot = Join-Path $StoragePermissionPath ('acceptance-permission-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $permissionRoot | Out-Null
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
        Remove-Item -LiteralPath $permissionRoot -Recurse -Force
    }
}

try {
    $result.Target.IsAdministrator = Test-Administrator
    if (-not $result.Target.IsAdministrator) { throw 'The installer driver requires an elevated process.' }
    if ($ExecutionMode -ne 'Local') { throw 'This physical-machine driver only supports ExecutionMode=Local.' }
    $acceptanceRoot = [Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_ACCEPTANCE_TEST_ROOT', 'Process')
    if ([string]::IsNullOrWhiteSpace($acceptanceRoot) -or -not (Test-Path -LiteralPath $acceptanceRoot -PathType Container)) { throw 'The driver was not given an existing approved acceptance root.' }
    $acceptanceRoot = [IO.Path]::GetFullPath($acceptanceRoot).TrimEnd('\\')
    $blockedRoots = @('C:', 'C:\\', $env:WINDIR, $env:ProgramFiles, $env:ProgramData) | ForEach-Object { try { [IO.Path]::GetFullPath($_).TrimEnd('\\') } catch { $_ } }
    if ($acceptanceRoot -match '^[A-Za-z]:$' -or $acceptanceRoot -in $blockedRoots) { throw "The driver refused a system or volume acceptance root: $acceptanceRoot" }
    $marker = Join-Path $acceptanceRoot '.storage-chronicle-testlab-marker.json'
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw "The acceptance root lacks the required marker: $marker" }
    $result.Target.Isolated = $true
    $result.Target.TestDataRoot = $acceptanceRoot
    if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) { throw "Base MSI is missing: $MsiPath" }
    switch ($CaseId) {
        'clean-install' {
            Invoke-Msi -Action Install -PackagePath $MsiPath -EvidenceName $CaseId | Out-Null
            Assert-InstalledFiles
            Assert-InstalledService
            Assert-ProductEntry
        }
        'repair' {
            Invoke-Msi -Action Repair -PackagePath $MsiPath -EvidenceName $CaseId | Out-Null
            Assert-InstalledFiles
            Assert-InstalledService
        }
        'update' {
            if ((Get-InstalledProduct).Count -eq 0) { Invoke-Msi -Action Install -PackagePath $MsiPath -EvidenceName ($CaseId + '-install') | Out-Null }
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
            Assert-InstalledService
            Assert-ProductEntry
        }
        'rollback' {
            if ((Get-InstalledProduct).Count -eq 0) { Invoke-Msi -Action Install -PackagePath $MsiPath -EvidenceName ($CaseId + '-install') | Out-Null }
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
            if ((Get-InstalledProduct).Count -eq 0) { Invoke-Msi -Action Install -PackagePath $MsiPath -EvidenceName ($CaseId + '-reinstall') | Out-Null }
            New-Item -ItemType Directory -Force -Path $HistoryPath | Out-Null
            Invoke-Msi -Action Uninstall -PackagePath $MsiPath -EvidenceName $CaseId | Out-Null
            $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
            $evidence = Write-Evidence -Name 'uninstall-state' -Value ([ordered]@{ ServicePresent = $null -ne $service; HistoryPresent = Test-Path -LiteralPath $HistoryPath -PathType Container; ProductCount = (Get-InstalledProduct).Count })
            Add-Assertion -Name 'Uninstall removes product registration and service but retains history directory' -Passed ($null -eq $service -and (Get-InstalledProduct).Count -eq 0 -and (Test-Path -LiteralPath $HistoryPath -PathType Container)) -Details 'The service/product were removed while the permanent history directory remained.' -EvidencePath $evidence
        }
        'history-retention' {
            if ((Get-InstalledProduct).Count -eq 0) { Invoke-Msi -Action Install -PackagePath $MsiPath -EvidenceName ($CaseId + '-install') | Out-Null }
            New-Item -ItemType Directory -Force -Path $HistoryPath | Out-Null
            $marker = Join-Path $HistoryPath ('acceptance-retention-' + [guid]::NewGuid().ToString('N') + '.marker')
            New-Item -ItemType File -Path $marker -Force | Out-Null
            Invoke-Msi -Action Uninstall -PackagePath $MsiPath -EvidenceName $CaseId | Out-Null
            $evidence = Write-Evidence -Name 'history-retention' -Value ([ordered]@{ MarkerPath = $marker; MarkerPresentAfterUninstall = Test-Path -LiteralPath $marker -PathType Leaf; HistoryRootPresent = Test-Path -LiteralPath $HistoryPath -PathType Container })
            Add-Assertion -Name 'History marker survives uninstall' -Passed ((Test-Path -LiteralPath $marker -PathType Leaf) -and (Test-Path -LiteralPath $HistoryPath -PathType Container)) -Details 'Only an empty acceptance marker was created; existing history was not deleted.' -EvidencePath $evidence
        }
        'service' {
            if ((Get-InstalledProduct).Count -eq 0) { Invoke-Msi -Action Install -PackagePath $MsiPath -EvidenceName ($CaseId + '-install') | Out-Null }
            Assert-InstalledService
        }
        'session' {
            if ((Get-InstalledProduct).Count -eq 0) { Invoke-Msi -Action Install -PackagePath $MsiPath -EvidenceName ($CaseId + '-install') | Out-Null }
            Assert-SessionAgent
        }
        'non-admin' {
            if ((Get-InstalledProduct).Count -eq 0) { Invoke-Msi -Action Install -PackagePath $MsiPath -EvidenceName ($CaseId + '-install') | Out-Null }
            $details = Invoke-NonAdminProcess -FilePath (Join-Path $InstallPath 'StorageChronicle.UI.Desktop.exe')
            $evidence = Write-Evidence -Name 'non-admin-ui' -Value $details
            Add-Assertion -Name 'UI launches as the declared non-admin user' -Passed (-not $details.HasExited) -Details "User=$($details.User); process=$($details.ProcessId) remained alive during the startup window." -EvidencePath $evidence
        }
        'storage-permission' {
            if ((Get-InstalledProduct).Count -eq 0) { Invoke-Msi -Action Install -PackagePath $MsiPath -EvidenceName ($CaseId + '-install') | Out-Null }
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
$result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
if ($LogPath) { $result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $LogPath -Encoding UTF8 }
exit $(if ($result.Status -eq 'PASSED') { 0 } else { 1 })
