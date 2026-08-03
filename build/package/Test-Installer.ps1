[CmdletBinding()]
param(
    [string]$MsiPath,
    [string]$UpdatedMsiPath,
    [string]$RollbackMsiPath,
    [string]$TargetOs,
    [string]$ExecutionMode,
    [string]$DriverScript,
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
    [string]$OutputDirectory,
    [int]$CaseTimeoutSeconds = 1800,
    [switch]$Execute,
    [switch]$AllowLocalIsolatedExecution
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff', [Globalization.CultureInfo]::InvariantCulture)

function Get-EnvironmentFallback {
    param(
        [AllowNull()][string]$Value,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        return $Value
    }

    return [Environment]::GetEnvironmentVariable($Name, 'Process')
}

$MsiPath = Get-EnvironmentFallback $MsiPath 'STORAGE_CHRONICLE_INSTALLER_MSI'
$UpdatedMsiPath = Get-EnvironmentFallback $UpdatedMsiPath 'STORAGE_CHRONICLE_INSTALLER_UPDATED_MSI'
$RollbackMsiPath = Get-EnvironmentFallback $RollbackMsiPath 'STORAGE_CHRONICLE_INSTALLER_ROLLBACK_MSI'
$TargetOs = Get-EnvironmentFallback $TargetOs 'STORAGE_CHRONICLE_INSTALLER_TARGET_OS'
$ExecutionMode = Get-EnvironmentFallback $ExecutionMode 'STORAGE_CHRONICLE_INSTALLER_MODE'
$DriverScript = Get-EnvironmentFallback $DriverScript 'STORAGE_CHRONICLE_INSTALLER_DRIVER'
$VmName = Get-EnvironmentFallback $VmName 'STORAGE_CHRONICLE_INSTALLER_VM_NAME'
$WindowsIsoPath = Get-EnvironmentFallback $WindowsIsoPath 'STORAGE_CHRONICLE_INSTALLER_WINDOWS_ISO'
$NonAdminUser = Get-EnvironmentFallback $NonAdminUser 'STORAGE_CHRONICLE_INSTALLER_NONADMIN_USER'
$NonAdminCredentialReference = Get-EnvironmentFallback $NonAdminCredentialReference 'STORAGE_CHRONICLE_INSTALLER_NONADMIN_CREDENTIAL_REF'
$SessionUser = Get-EnvironmentFallback $SessionUser 'STORAGE_CHRONICLE_INSTALLER_SESSION_USER'
$ServiceCredentialReference = Get-EnvironmentFallback $ServiceCredentialReference 'STORAGE_CHRONICLE_INSTALLER_SERVICE_CREDENTIAL_REF'
$HistoryPath = Get-EnvironmentFallback $HistoryPath 'STORAGE_CHRONICLE_INSTALLER_HISTORY_PATH'
$InstallPath = Get-EnvironmentFallback $InstallPath 'STORAGE_CHRONICLE_INSTALLER_INSTALL_PATH'
$StoragePermissionPath = Get-EnvironmentFallback $StoragePermissionPath 'STORAGE_CHRONICLE_INSTALLER_STORAGE_PERMISSION_PATH'
$OutputDirectory = Get-EnvironmentFallback $OutputDirectory 'STORAGE_CHRONICLE_INSTALLER_OUTPUT'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'artifacts/installer/acceptance'
} elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $root $OutputDirectory
}

$caseDefinitions = @(
    [ordered]@{ Id = 'clean-install'; Name = 'Clean install'; Requirements = @('MsiPath') },
    [ordered]@{ Id = 'repair'; Name = 'Repair'; Requirements = @('MsiPath') },
    [ordered]@{ Id = 'update'; Name = 'Update'; Requirements = @('MsiPath', 'UpdatedMsiPath') },
    [ordered]@{ Id = 'rollback'; Name = 'Rollback'; Requirements = @('MsiPath', 'UpdatedMsiPath', 'RollbackMsiPath') },
    [ordered]@{ Id = 'uninstall'; Name = 'Uninstall'; Requirements = @('MsiPath') },
    [ordered]@{ Id = 'history-retention'; Name = 'History retention'; Requirements = @('MsiPath', 'HistoryPath') },
    [ordered]@{ Id = 'service'; Name = 'LocalSystem service and recovery'; Requirements = @('MsiPath', 'ServiceName', 'ServiceCredentialReference') },
    [ordered]@{ Id = 'session'; Name = 'Session Agent startup'; Requirements = @('MsiPath', 'SessionUser') },
    [ordered]@{ Id = 'non-admin'; Name = 'Non-admin UI'; Requirements = @('MsiPath', 'NonAdminUser', 'NonAdminCredentialReference') },
    [ordered]@{ Id = 'storage-permission'; Name = 'Storage permission'; Requirements = @('MsiPath', 'StoragePermissionPath') }
)

$manifestPath = Join-Path $OutputDirectory "installer-acceptance-$runId.json"
$markdownPath = Join-Path $OutputDirectory "installer-acceptance-$runId.md"
$caseDirectory = Join-Path $OutputDirectory "installer-acceptance-$runId"

$script:Preflight = @()
$script:CaseResults = New-Object System.Collections.ArrayList
$script:Errors = @()
$script:Manifest = [ordered]@{
    Schema = 'storage-chronicle.installer-acceptance.v1'
    RunId = $runId
    StartedUtc = [DateTime]::UtcNow.ToString('O')
    CompletedUtc = $null
    Status = 'RUNNING'
    ExitCode = $null
    TargetOs = $TargetOs
    ExecutionMode = $ExecutionMode
    ExecuteRequested = [bool]$Execute
    Environment = [ordered]@{
        Host = [Environment]::OSVersion.VersionString
        TargetOs = $TargetOs
        ExecutionMode = $ExecutionMode
        MsiPath = $MsiPath
        UpdatedMsiPath = $UpdatedMsiPath
        RollbackMsiPath = $RollbackMsiPath
        DriverScript = $DriverScript
        VmName = $VmName
        WindowsIsoPath = $WindowsIsoPath
        ServiceName = $ServiceName
        NonAdminUser = $NonAdminUser
        SessionUser = $SessionUser
        HistoryPath = $HistoryPath
        InstallPath = $InstallPath
        StoragePermissionPath = $StoragePermissionPath
        CredentialReferences = [ordered]@{
            NonAdmin = $NonAdminCredentialReference
            Service = $ServiceCredentialReference
        }
    }
    Preconditions = @()
    Tests = @()
    Errors = @()
    Artifacts = [ordered]@{
        Json = $manifestPath
        Markdown = $markdownPath
        CaseDirectory = $caseDirectory
    }
}

function Add-Precondition {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$Ready,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    $status = if ($Ready) { 'READY' } else { 'NOT_EXECUTED' }
    $script:Preflight += [ordered]@{
        Name = $Name
        Status = $status
        Ready = $Ready
        Reason = $Reason
    }
}

function Add-CaseResult {
    param(
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][ValidateSet('PASSED', 'FAILED', 'NOT_EXECUTED')][string]$Status,
        [string]$Reason,
        [Nullable[int]]$ExitCode = $null,
        [Nullable[double]]$DurationSeconds = $null,
        [string]$ResultPath,
        [string]$LogPath,
        [object]$Assertions,
        [object]$Target
    )

    $assertionList = New-Object System.Collections.ArrayList
    if ($null -ne $Assertions) {
        foreach ($assertion in @($Assertions)) {
            if ($null -ne $assertion) {
                [void]$assertionList.Add($assertion)
            }
        }
    }
    $record = [ordered]@{
        Id = $Id
        Name = $Name
        Status = $Status
        Reason = $Reason
        ExitCode = $ExitCode
        DurationSeconds = $DurationSeconds
        ResultPath = $ResultPath
        LogPath = $LogPath
        Assertions = $assertionList
        Target = $Target
    }
    [void]$script:CaseResults.Add([pscustomobject]$record)
    Write-Host ("{0} [{1}] {2}" -f $Status, $Id, $(if ([string]::IsNullOrWhiteSpace($Reason)) { $Name } else { $Reason })) -ForegroundColor $(switch ($Status) { 'PASSED' { 'Green' } 'FAILED' { 'Red' } default { 'Yellow' } })
}

function Test-IsWindows {
    try {
        return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
    } catch {
        return $env:OS -eq 'Windows_NT'
    }
}

function Test-IsAdministrator {
    if (-not (Test-IsWindows)) {
        return $false
    }

    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {
        return $false
    }
}

function Test-ExistingFile {
    param([AllowNull()][string]$Path)
    return -not [string]::IsNullOrWhiteSpace($Path) -and (Test-Path -LiteralPath $Path -PathType Leaf)
}

function Test-MsiFile {
    param([AllowNull()][string]$Path)
    if (-not (Test-ExistingFile $Path)) {
        return $false
    }

    return [string]::Equals([IO.Path]::GetExtension($Path), '.msi', [StringComparison]::OrdinalIgnoreCase)
}

function Test-IsoFile {
    param([AllowNull()][string]$Path)
    return (Test-ExistingFile $Path) -and [string]::Equals([IO.Path]::GetExtension($Path), '.iso', [StringComparison]::OrdinalIgnoreCase)
}

function Get-CurrentWindowsTarget {
    if (-not (Test-IsWindows)) {
        return $null
    }

    try {
        $operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        $caption = [string]$operatingSystem.Caption
        $displayVersion = [string]$operatingSystem.DisplayVersion
        $build = [string]$operatingSystem.BuildNumber
        if ($caption -match 'Windows 11') {
            return 'Windows11'
        }

        if ($caption -match 'Windows 10' -and ($displayVersion -eq '22H2' -or $build -eq '19045')) {
            return 'Windows10-22H2'
        }
    } catch {
        return $null
    }

    return $null
}

function Test-DriverLauncher {
    param([AllowNull()][string]$Path)
    if (-not (Test-ExistingFile $Path)) {
        return $false
    }

    $extension = [IO.Path]::GetExtension($Path)
    if ($extension -eq '.ps1') {
        return $null -ne (Get-Command powershell.exe -ErrorAction SilentlyContinue) -or $null -ne (Get-Command pwsh -ErrorAction SilentlyContinue)
    }

    return $extension -in @('.exe', '.cmd', '.bat')
}

function Get-Precondition {
    param([Parameter(Mandatory = $true)][string]$Name)
    return @($script:Preflight | Where-Object { $_.Name -eq $Name } | Select-Object -First 1)
}

function Get-PreconditionReasons {
    param([Parameter(Mandatory = $true)][string[]]$Names)
    $reasons = New-Object System.Collections.Generic.List[string]
    foreach ($name in $Names) {
        $precondition = Get-Precondition $name
        if ($null -eq $precondition -or -not $precondition.Ready) {
            if ($null -eq $precondition) {
                [void]$reasons.Add("Missing precondition: $name")
            } else {
                [void]$reasons.Add("$($precondition.Name): $($precondition.Reason)")
            }
        }
    }
    return @($reasons)
}

function ConvertTo-WindowsProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    if ($Value -notmatch '[\s"]' -and $Value.Length -gt 0) {
        return $Value
    }

    $escaped = $Value -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $argumentListProperty = [System.Diagnostics.ProcessStartInfo].GetProperty('ArgumentList')
    if ($null -ne $argumentListProperty -and $null -ne $startInfo.ArgumentList) {
        foreach ($argument in $Arguments) {
            [void]$startInfo.ArgumentList.Add([string]$argument)
        }
    } else {
        $startInfo.Arguments = (($Arguments | ForEach-Object { ConvertTo-WindowsProcessArgument ([string]$_) }) -join ' ')
    }

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start process: $FilePath"
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $timeoutMilliseconds = $TimeoutSeconds * 1000
        $completed = $process.WaitForExit($timeoutMilliseconds)
        if (-not $completed) {
            try { $process.Kill() } catch { }
            $process.WaitForExit()
            return [pscustomobject]@{
                ExitCode = 124
                TimedOut = $true
                Output = $stdoutTask.GetAwaiter().GetResult()
                Error = $stderrTask.GetAwaiter().GetResult()
            }
        }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            TimedOut = $false
            Output = $stdoutTask.GetAwaiter().GetResult()
            Error = $stderrTask.GetAwaiter().GetResult()
        }
    } finally {
        $process.Dispose()
    }
}

function Get-DriverInvocation {
    param(
        [Parameter(Mandatory = $true)][string]$CaseId,
        [Parameter(Mandatory = $true)][string]$ResultPath,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $driverArguments = @(
        '-CaseId', $CaseId,
        '-TargetOs', $TargetOs,
        '-ExecutionMode', $ExecutionMode,
        '-MsiPath', $MsiPath,
        '-UpdatedMsiPath', $UpdatedMsiPath,
        '-RollbackMsiPath', $RollbackMsiPath,
        '-VmName', $VmName,
        '-WindowsIsoPath', $WindowsIsoPath,
        '-ServiceName', $ServiceName,
        '-NonAdminUser', $NonAdminUser,
        '-NonAdminCredentialReference', $NonAdminCredentialReference,
        '-SessionUser', $SessionUser,
        '-ServiceCredentialReference', $ServiceCredentialReference,
        '-HistoryPath', $HistoryPath,
        '-InstallPath', $InstallPath,
        '-StoragePermissionPath', $StoragePermissionPath,
        '-ResultPath', $ResultPath,
        '-LogPath', $LogPath
    )

    $extension = [IO.Path]::GetExtension($DriverScript)
    if ($extension -eq '.ps1') {
        $powershell = Get-Command powershell.exe -ErrorAction SilentlyContinue
        if ($null -eq $powershell) {
            $powershell = Get-Command pwsh -ErrorAction Stop
        }
        return [pscustomobject]@{
            FilePath = $powershell.Source
            Arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $DriverScript) + $driverArguments
        }
    }

    if ($extension -in @('.cmd', '.bat')) {
        return [pscustomobject]@{
            FilePath = (Get-Command cmd.exe -ErrorAction Stop).Source
            Arguments = @('/d', '/s', '/c', $DriverScript) + $driverArguments
        }
    }

    return [pscustomobject]@{
        FilePath = $DriverScript
        Arguments = $driverArguments
    }
}

function Read-DriverResult {
    param(
        [Parameter(Mandatory = $true)][string]$CaseId,
        [Parameter(Mandatory = $true)][string]$ResultPath
    )

    if (-not (Test-ExistingFile $ResultPath)) {
        throw "Driver did not create its result JSON: $ResultPath"
    }

    $payload = Get-Content -Raw -Encoding UTF8 -LiteralPath $ResultPath | ConvertFrom-Json
    if ($null -eq $payload) {
        throw "Driver result is empty: $ResultPath"
    }
    if ([string]$payload.CaseId -ne $CaseId) {
        throw "Driver result CaseId '$($payload.CaseId)' does not match '$CaseId'."
    }
    if ([string]$payload.Status -notin @('PASSED', 'FAILED', 'NOT_EXECUTED')) {
        throw "Driver result has an invalid Status for '$CaseId': $($payload.Status)"
    }

    if ([string]$payload.Status -eq 'NOT_EXECUTED') {
        if ([string]::IsNullOrWhiteSpace([string]$payload.Reason)) {
            throw "Driver result marked '$CaseId' NOT_EXECUTED without a Reason."
        }
        return [pscustomobject]@{
            Status = 'NOT_EXECUTED'
            Reason = [string]$payload.Reason
            Assertions = @($payload.Assertions)
            Target = $payload.Target
        }
    }

    if ([string]$payload.Status -eq 'FAILED') {
        return [pscustomobject]@{
            Status = 'FAILED'
            Reason = if ([string]::IsNullOrWhiteSpace([string]$payload.Reason)) { 'The installer driver reported failure.' } else { [string]$payload.Reason }
            Assertions = @($payload.Assertions)
            Target = $payload.Target
        }
    }

    if ($null -eq $payload.Target -or [string]$payload.Target.OS -ne $TargetOs -or [string]$payload.Target.Mode -ne $ExecutionMode -or $payload.Target.Isolated -ne $true -or $payload.Target.IsAdministrator -ne $true) {
        throw "Driver result '$CaseId' lacks a matching isolated, administrator target declaration."
    }
    if ($ExecutionMode -eq 'VM' -and [string]::IsNullOrWhiteSpace([string]$payload.Target.VmName)) {
        throw "Driver result '$CaseId' lacks the VM name in Target.VmName."
    }

    $assertions = @($payload.Assertions)
    if ($assertions.Count -eq 0) {
        throw "Driver result '$CaseId' has no assertions."
    }

    $evidence = New-Object System.Collections.Generic.List[string]
    foreach ($assertion in $assertions) {
        if ([string]::IsNullOrWhiteSpace([string]$assertion.Name) -or [string]$assertion.Status -ne 'PASSED') {
            throw "Driver result '$CaseId' contains an assertion that did not pass."
        }
        $evidencePath = [string]$assertion.EvidencePath
        if ([string]::IsNullOrWhiteSpace($evidencePath) -or -not (Test-Path -LiteralPath $evidencePath)) {
            throw "Driver result '$CaseId' references missing evidence: $evidencePath"
        }
        [void]$evidence.Add($evidencePath)
    }

    return [pscustomobject]@{
        Status = 'PASSED'
        Reason = 'All driver assertions passed with host-visible evidence.'
        Assertions = $assertions
        EvidencePaths = @($evidence)
        Target = $payload.Target
    }
}

function Invoke-InstallerCase {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Definition
    )

    $caseId = [string]$Definition.Id
    $caseResultPath = Join-Path $caseDirectory "$caseId.json"
    $caseLogPath = Join-Path $caseDirectory "$caseId.log"
    $reasons = Get-PreconditionReasons @('WindowsHost', 'ExecutionArmed', 'TargetOs', 'ExecutionMode', 'Driver', 'CaseTimeout', 'HostAdministrator', 'Isolation', 'MsiPath')
    $reasons += Get-PreconditionReasons $Definition.Requirements
    if ($reasons.Count -gt 0) {
        Add-CaseResult -Id $caseId -Name ([string]$Definition.Name) -Status 'NOT_EXECUTED' -Reason (($reasons | Select-Object -Unique) -join '; ')
        return
    }

    $started = [DateTime]::UtcNow
    Write-Host "RUNNING [$caseId]" -ForegroundColor Cyan
    try {
        $invocation = Get-DriverInvocation -CaseId $caseId -ResultPath $caseResultPath -LogPath $caseLogPath
        $result = Invoke-CapturedProcess -FilePath $invocation.FilePath -Arguments $invocation.Arguments -TimeoutSeconds $CaseTimeoutSeconds
        $driverLog = "STDOUT`r`n$($result.Output)`r`nSTDERR`r`n$($result.Error)"
        Set-Content -LiteralPath $caseLogPath -Value $driverLog -Encoding UTF8
        if ($result.TimedOut) {
            Add-CaseResult -Id $caseId -Name ([string]$Definition.Name) -Status 'FAILED' -Reason "Driver timed out after $CaseTimeoutSeconds seconds." -ExitCode $result.ExitCode -DurationSeconds (([DateTime]::UtcNow - $started).TotalSeconds) -ResultPath $caseResultPath -LogPath $caseLogPath
            return
        }
        if ($result.ExitCode -ne 0) {
            $reason = "Driver exited with code $($result.ExitCode)."
            if (-not [string]::IsNullOrWhiteSpace([string]$result.Error)) {
                $reason += ' ' + ([string]$result.Error).Trim().Substring(0, [Math]::Min(1000, ([string]$result.Error).Trim().Length))
            }
            Add-CaseResult -Id $caseId -Name ([string]$Definition.Name) -Status 'FAILED' -Reason $reason -ExitCode $result.ExitCode -DurationSeconds (([DateTime]::UtcNow - $started).TotalSeconds) -ResultPath $caseResultPath -LogPath $caseLogPath
            return
        }

        $driverResult = Read-DriverResult -CaseId $caseId -ResultPath $caseResultPath
        Add-CaseResult -Id $caseId -Name ([string]$Definition.Name) -Status $driverResult.Status -Reason $driverResult.Reason -ExitCode $result.ExitCode -DurationSeconds (([DateTime]::UtcNow - $started).TotalSeconds) -ResultPath $caseResultPath -LogPath $caseLogPath -Assertions $driverResult.Assertions -Target $driverResult.Target
    } catch {
        Add-CaseResult -Id $caseId -Name ([string]$Definition.Name) -Status 'FAILED' -Reason $_.Exception.Message -ExitCode 1 -DurationSeconds (([DateTime]::UtcNow - $started).TotalSeconds) -ResultPath $caseResultPath -LogPath $caseLogPath
    }
}

function Add-MissingCaseResults {
    param([Parameter(Mandatory = $true)][string]$Reason)
    foreach ($definition in $caseDefinitions) {
        if (@($script:CaseResults | Where-Object { $_.Id -eq $definition.Id }).Count -eq 0) {
            Add-CaseResult -Id ([string]$definition.Id) -Name ([string]$definition.Name) -Status 'NOT_EXECUTED' -Reason $Reason
        }
    }
}

function Escape-MarkdownCell {
    param([AllowNull()][object]$Value)
    if ($null -eq $Value) {
        return ''
    }
    return ([string]$Value).Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
}

function Write-MarkdownArtifact {
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine('# Installer acceptance result')
    [void]$builder.AppendLine()
    [void]$builder.AppendLine(('- Run ID: `{0}`' -f $script:Manifest.RunId))
    [void]$builder.AppendLine(('- Status: **{0}**' -f $script:Manifest.Status))
    [void]$builder.AppendLine(('- Exit code: `{0}`' -f $script:Manifest.ExitCode))
    [void]$builder.AppendLine(('- Target OS: `{0}`' -f $script:Manifest.TargetOs))
    [void]$builder.AppendLine(('- Execution mode: `{0}`' -f $script:Manifest.ExecutionMode))
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('This is an acceptance result, not an MSI build result. The run is successful only when every listed case is `PASSED` with an isolated target declaration and host-visible evidence. `NOT_EXECUTED` is never treated as success.')
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('## Preconditions')
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('| Name | Status | Reason |')
    [void]$builder.AppendLine('| --- | --- | --- |')
    foreach ($precondition in @($script:Preflight)) {
        [void]$builder.AppendLine("| $(Escape-MarkdownCell $precondition.Name) | $(Escape-MarkdownCell $precondition.Status) | $(Escape-MarkdownCell $precondition.Reason) |")
    }
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('## Cases')
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('| Case | Status | Exit code | Duration (s) | Reason | Evidence/result |')
    [void]$builder.AppendLine('| --- | --- | ---: | ---: | --- | --- |')
    foreach ($case in @($script:Manifest.Tests)) {
        $evidence = @($case.Assertions | ForEach-Object { $_.EvidencePath } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $evidenceText = if ($evidence.Count -gt 0) { $evidence -join '<br>' } elseif (-not [string]::IsNullOrWhiteSpace([string]$case.ResultPath)) { [string]$case.ResultPath } else { '' }
        $caseLine = '| {0} (`{1}`) | {2} | {3} | {4} | {5} | {6} |' -f (Escape-MarkdownCell $case.Name), (Escape-MarkdownCell $case.Id), (Escape-MarkdownCell $case.Status), (Escape-MarkdownCell $case.ExitCode), (Escape-MarkdownCell $case.DurationSeconds), (Escape-MarkdownCell $case.Reason), (Escape-MarkdownCell $evidenceText)
        [void]$builder.AppendLine($caseLine)
    }
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('## Artifacts')
    [void]$builder.AppendLine()
    [void]$builder.AppendLine(('- JSON: `{0}`' -f $script:Manifest.Artifacts.Json))
    [void]$builder.AppendLine(('- Case directory: `{0}`' -f $script:Manifest.Artifacts.CaseDirectory))
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('## Driver contract')
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('The driver supplied through `-DriverScript` or `STORAGE_CHRONICLE_INSTALLER_DRIVER` must perform the case on the declared Windows target and write the requested result JSON. A passing result must contain the matching `CaseId`, `Status: PASSED`, `Target.OS`, `Target.Mode`, `Target.Isolated: true`, `Target.IsAdministrator: true`, and at least one passing assertion with an existing `EvidencePath`. For VM runs, `Target.VmName` is also required. The harness does not synthesize or infer a pass from an exit code alone.')
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('## Exit codes')
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('- `0`: every case passed and every required precondition was ready.')
    [void]$builder.AppendLine('- `1`: a case or the harness failed.')
    [void]$builder.AppendLine('- `2`: one or more cases were `NOT_EXECUTED` because the target, ISO/VM, driver, artifact, or required privilege was unavailable.')
    Set-Content -LiteralPath $markdownPath -Value $builder.ToString() -Encoding UTF8
}

try {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    New-Item -ItemType Directory -Force -Path $caseDirectory | Out-Null

    $isWindows = Test-IsWindows
    $isAdministrator = Test-IsAdministrator
    $currentTarget = Get-CurrentWindowsTarget
    $validTarget = $TargetOs -in @('Windows10-22H2', 'Windows11')
    $validMode = $ExecutionMode -in @('Local', 'VM')
    $driverReady = Test-DriverLauncher $DriverScript
    $hyperVReady = $false
    $vmReady = $false
    if ($validMode -and $ExecutionMode -eq 'VM' -and $isWindows) {
        $getVm = Get-Command Get-VM -ErrorAction SilentlyContinue
        $hyperVReady = $null -ne $getVm
        if ($hyperVReady -and -not [string]::IsNullOrWhiteSpace($VmName)) {
            try {
                $vm = Get-VM -Name $VmName -ErrorAction Stop
                $vmReady = [string]$vm.State -eq 'Running'
            } catch {
                $vmReady = $false
            }
        }
    }

    Add-Precondition 'WindowsHost' $isWindows 'The installer acceptance harness requires a Windows host because MSI, service, session, and ACL checks are Windows-only.'
    Add-Precondition 'ExecutionArmed' ([bool]$Execute) 'The run is armed only when -Execute is supplied; planning or omitted execution never passes.'
    Add-Precondition 'TargetOs' ($validTarget) 'TargetOs must be Windows10-22H2 or Windows11 and must be supplied explicitly.'
    Add-Precondition 'ExecutionMode' ($validMode) 'ExecutionMode must be Local or VM and must be supplied explicitly.'
    Add-Precondition 'Driver' $driverReady 'A real installer driver script or executable is required; no fake/default driver is provided.'
    Add-Precondition 'CaseTimeout' ($CaseTimeoutSeconds -ge 1 -and $CaseTimeoutSeconds -le 7200) 'CaseTimeoutSeconds must be between 1 and 7200 seconds.'
    Add-Precondition 'ServiceName' (-not [string]::IsNullOrWhiteSpace($ServiceName)) 'The installed Agent service name is required.'
    Add-Precondition 'HistoryPath' (-not [string]::IsNullOrWhiteSpace($HistoryPath)) 'A guest history path is required for retention verification.'
    Add-Precondition 'InstallPath' (-not [string]::IsNullOrWhiteSpace($InstallPath)) 'A guest install path is required for install and update verification.'
    Add-Precondition 'StoragePermissionPath' (-not [string]::IsNullOrWhiteSpace($StoragePermissionPath)) 'A guest path for the denied-storage permission probe is required.'
    Add-Precondition 'NonAdminUser' (-not [string]::IsNullOrWhiteSpace($NonAdminUser)) 'An existing non-administrator account is required for the non-admin UI case.'
    Add-Precondition 'NonAdminCredentialReference' (-not [string]::IsNullOrWhiteSpace($NonAdminCredentialReference)) 'A non-admin credential reference is required; plaintext passwords are not accepted by this harness.'
    Add-Precondition 'SessionUser' (-not [string]::IsNullOrWhiteSpace($SessionUser)) 'An interactive session user is required for Session Agent startup verification.'
    Add-Precondition 'ServiceCredentialReference' (-not [string]::IsNullOrWhiteSpace($ServiceCredentialReference) -or ($validMode -and $ExecutionMode -eq 'Local' -and $isAdministrator)) 'A service-capable credential reference is required for VM runs, or the current administrator must be used for an explicitly isolated local run.'
    Add-Precondition 'HostAdministrator' $isAdministrator 'Administrator/service privileges are required to control MSI installation, SCM, ACL, and VM acceptance operations.'
    Add-Precondition 'MsiPath' (Test-MsiFile $MsiPath) 'The base MSI path must point to an existing .msi file.'
    Add-Precondition 'UpdatedMsiPath' (Test-MsiFile $UpdatedMsiPath) 'The updated MSI path must point to an existing .msi file for update and rollback.'
    Add-Precondition 'RollbackMsiPath' (Test-MsiFile $RollbackMsiPath) 'The rollback MSI path must point to an existing .msi file from the prior product version.'

    if ($validMode -and $ExecutionMode -eq 'Local') {
        Add-Precondition 'Isolation' ([bool]$AllowLocalIsolatedExecution) 'Local execution requires explicit -AllowLocalIsolatedExecution and must be performed only on a disposable machine.'
        Add-Precondition 'LocalTargetOs' ($isWindows -and $currentTarget -eq $TargetOs) "The live host target '$currentTarget' does not match the requested '$TargetOs'."
    } elseif ($validMode -and $ExecutionMode -eq 'VM') {
        Add-Precondition 'Isolation' ($hyperVReady -and $vmReady) 'VM execution requires Hyper-V and a running named VM; the driver must reset an isolated snapshot per case.'
        Add-Precondition 'VmName' (-not [string]::IsNullOrWhiteSpace($VmName)) 'A named VM is required for VM execution.'
        Add-Precondition 'WindowsIsoPath' (Test-IsoFile $WindowsIsoPath) 'A Windows ISO file is required to identify/recreate the target VM environment; absence is NOT_EXECUTED.'
    } else {
        Add-Precondition 'Isolation' $false 'Isolation cannot be established until a valid execution mode is supplied.'
    }

    $script:Manifest.Preconditions = @($script:Preflight)
    $globalNames = @('WindowsHost', 'ExecutionArmed', 'TargetOs', 'ExecutionMode', 'Driver', 'CaseTimeout', 'HostAdministrator', 'Isolation', 'MsiPath')
    $globalReasons = Get-PreconditionReasons $globalNames
    if ($globalReasons.Count -gt 0) {
        $reason = ($globalReasons | Select-Object -Unique) -join '; '
        Add-MissingCaseResults $reason
        $script:ExitCode = 2
    } else {
        foreach ($definition in $caseDefinitions) {
            Invoke-InstallerCase -Definition $definition
        }
        $statuses = @($script:CaseResults | ForEach-Object { $_.Status })
        if ($statuses -contains 'FAILED') {
            $script:ExitCode = 1
        } elseif ($statuses -contains 'NOT_EXECUTED') {
            $script:ExitCode = 2
        } else {
            $script:ExitCode = 0
        }
    }
} catch {
    $script:ExitCode = 1
    $script:Errors += $_.Exception.Message
    Add-MissingCaseResults ('Harness failure: ' + $_.Exception.Message)
}

try {
    Add-MissingCaseResults 'The case was not reached because an earlier harness failure stopped execution.'
    $script:Manifest.CompletedUtc = [DateTime]::UtcNow.ToString('O')
    $script:Manifest.Status = if ($script:ExitCode -eq 0) { 'PASSED' } elseif ($script:ExitCode -eq 2) { 'NOT_EXECUTED' } else { 'FAILED' }
    $script:Manifest.ExitCode = $script:ExitCode
    $script:Manifest.Preconditions = @($script:Preflight)
    $script:Manifest.Tests = @($script:CaseResults)
    $script:Manifest.Errors = @($script:Errors)
    $summary = [ordered]@{
        Total = @($script:Manifest.Tests).Count
        Passed = @($script:Manifest.Tests | Where-Object { $_.Status -eq 'PASSED' }).Count
        Failed = @($script:Manifest.Tests | Where-Object { $_.Status -eq 'FAILED' }).Count
        NotExecuted = @($script:Manifest.Tests | Where-Object { $_.Status -eq 'NOT_EXECUTED' }).Count
    }
    $script:Manifest.Summary = $summary
    $json = $script:Manifest | ConvertTo-Json -Depth 20
    Set-Content -LiteralPath $manifestPath -Value $json -Encoding UTF8
    Write-MarkdownArtifact
    Write-Host "Installer acceptance JSON: $manifestPath" -ForegroundColor Cyan
    Write-Host "Installer acceptance Markdown: $markdownPath" -ForegroundColor Cyan
    Write-Host "Overall: $($script:Manifest.Status) (exit code $($script:ExitCode))" -ForegroundColor $(if ($script:ExitCode -eq 0) { 'Green' } elseif ($script:ExitCode -eq 2) { 'Yellow' } else { 'Red' })
} catch {
    Write-Error "Could not write installer acceptance artifacts: $($_.Exception.Message)"
    $script:ExitCode = 1
}

exit $script:ExitCode
