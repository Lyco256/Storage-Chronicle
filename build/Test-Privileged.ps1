[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$AcceptanceRoot,
    [string]$VhdxPath,
    [string]$VhdxRoot,
    [string]$DevicePath,
    [string]$RemovableRoot,
    [string]$SmbShareName,
    [string]$ServiceName = 'StorageChronicleAgent',
    [switch]$CreateVhdx,
    [switch]$CreateUsnJournal,
    [switch]$WaitForMediaChange,
    [int]$MediaTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $root 'tests/StorageChronicle.Platform.Windows.Integration.Tests/StorageChronicle.Platform.Windows.Integration.Tests.csproj'
$artifactRoot = Join-Path $root 'artifacts/acceptance'
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$manifestPath = Join-Path $artifactRoot "windows-privileged-$runId.json"
$runOutputPath = Join-Path $artifactRoot "windows-privileged-$runId.log"
$environmentNames = @(
    'STORAGE_CHRONICLE_ACCEPTANCE_ROOT',
    'STORAGE_CHRONICLE_ACCEPTANCE_DEVICE',
    'STORAGE_CHRONICLE_ACCEPTANCE_VHDX',
    'STORAGE_CHRONICLE_ACCEPTANCE_REMOVABLE_ROOT',
    'STORAGE_CHRONICLE_ACCEPTANCE_SMB_SHARE',
    'STORAGE_CHRONICLE_ACCEPTANCE_SERVICE',
    'STORAGE_CHRONICLE_ACCEPTANCE_WAIT_FOR_MEDIA')
$oldEnvironment = @{}
foreach ($name in $environmentNames) { $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }

$manifest = [ordered]@{
    RunId = $runId
    StartedUtc = [DateTime]::UtcNow.ToString('O')
    Configuration = $Configuration
    Status = 'RUNNING'
    ExitCode = $null
    Environment = [ordered]@{}
    Capabilities = @()
    Tests = @()
    Artifacts = [ordered]@{ Manifest = $manifestPath; Log = $runOutputPath }
}

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
Start-Transcript -Path $runOutputPath -Force | Out-Null

function Set-ProcessEnvironment([string]$Name, [string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        [Environment]::SetEnvironmentVariable($Name, $null, 'Process')
    } else {
        [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
    }
}

function Add-NotExecuted([string]$Capability, [string]$Reason) {
    $manifest.Tests += [ordered]@{ Capability = $Capability; Status = 'NOT_EXECUTED'; Reason = $Reason }
    Write-Host "NOT_EXECUTED [$Capability] $Reason" -ForegroundColor Yellow
}

function Invoke-Captured([string]$FilePath, [string[]]$Arguments) {
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FilePath
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $argumentListProperty = [System.Diagnostics.ProcessStartInfo].GetProperty('ArgumentList')
    if ($null -ne $argumentListProperty -and $null -ne $info.ArgumentList) {
        foreach ($argument in $Arguments) { [void]$info.ArgumentList.Add([string]$argument) }
    } else {
        $quotedArguments = foreach ($argument in $Arguments) {
            if ($argument -notmatch '[\s"]') {
                $argument
            } else {
                '"' + (($argument -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
            }
        }
        $info.Arguments = $quotedArguments -join ' '
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) { throw "Could not start $FilePath." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $captured = [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = $stdoutTask.GetAwaiter().GetResult()
        Error = $stderrTask.GetAwaiter().GetResult()
    }
    $process.Dispose()
    return $captured
}

function Get-VolumeForPath([string]$Path) {
    try {
        $volume = Get-Volume -Path $Path -ErrorAction Stop
        if ($null -ne $volume) { return $volume }
    } catch {
        # Get-Volume -Path is unavailable or unreliable for some provider-backed
        # paths (including OneDrive locations). Resolve only the existing drive
        # root as a safe, read-only fallback; the caller has already validated
        # that the acceptance directory exists.
    }

    try {
        $root = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Path))
        if ($root -match '^(?<drive>[A-Za-z]):\\$') {
            return Get-Volume -DriveLetter $Matches.drive -ErrorAction Stop
        }
    } catch {
        return $null
    }

    return $null
}

function Is-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Is-PathSafeForAcceptance([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($fullPath.Length -lt 4) { return $false }
    $forbidden = @(
        [IO.Path]::GetFullPath($env:WINDIR).TrimEnd('\'),
        [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\'),
        [IO.Path]::GetFullPath(${env:ProgramFiles(x86)}).TrimEnd('\'))
    foreach ($item in $forbidden) {
        if ($fullPath.Equals($item, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    }
    return $true
}

function Add-Capability([string]$Name, [bool]$Ready, [string]$Reason) {
    $manifest.Capabilities += [ordered]@{ Name = $Name; Ready = $Ready; Reason = $Reason }
    return [pscustomobject]@{ Name = $Name; Ready = $Ready; Reason = $Reason }
}

function Write-Manifest([int]$ExitCode, [string]$Status) {
    $manifest.Status = $Status
    $manifest.ExitCode = $ExitCode
    $manifest.CompletedUtc = [DateTime]::UtcNow.ToString('O')
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
}

$createdVhdx = $false
$mountedVhdx = $false
$exitCode = 0
try {
    $isWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
    if (-not $isWindows) {
        $manifest.Environment = [ordered]@{ OS = 'non-Windows'; Prerequisite = 'Windows is required' }
        Add-NotExecuted 'all' 'This acceptance suite requires a Windows host.'
        $exitCode = 2
        Write-Manifest $exitCode 'NOT_EXECUTED'
        exit $exitCode
    }

    if ($CreateVhdx) {
        if ([string]::IsNullOrWhiteSpace($VhdxPath)) { throw '-CreateVhdx requires -VhdxPath.' }
        $VhdxPath = [IO.Path]::GetFullPath($VhdxPath)
        if ([IO.Path]::GetExtension($VhdxPath) -ine '.vhdx') { throw '-VhdxPath must have a .vhdx extension.' }
        if (Test-Path -LiteralPath $VhdxPath) { throw "Refusing to overwrite an existing VHDX: $VhdxPath" }
        if (-not (Test-Path -LiteralPath (Split-Path -Parent $VhdxPath))) { throw 'The VHDX parent directory must already exist.' }
        foreach ($command in @('New-VHD', 'Mount-VHD', 'Get-Disk', 'Initialize-Disk', 'New-Partition', 'Format-Volume')) {
            if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "The Hyper-V/Storage command $command is not available; VHDX setup was not executed." }
        }

        New-VHD -Path $VhdxPath -SizeBytes 1GB -Dynamic | Out-Null
        $createdVhdx = $true
        $diskImage = Mount-VHD -Path $VhdxPath -Passthru
        $mountedVhdx = $true
        $disk = $diskImage | Get-Disk
        if ($disk.IsOffline) { Set-Disk -Number $disk.Number -IsOffline $false }
        if ($disk.IsReadOnly) { Set-Disk -Number $disk.Number -IsReadOnly $false }
        Initialize-Disk -Number $disk.Number -PartitionStyle GPT | Out-Null
        $partition = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
        Format-Volume -Partition $partition -FileSystem NTFS -NewFileSystemLabel 'StorageChronicleAcceptance' -Confirm:$false | Out-Null
        $VhdxRoot = "$($partition.DriveLetter):\"
        $AcceptanceRoot = $VhdxRoot
        Write-Host "Created and mounted disposable VHDX at $VhdxPath -> $AcceptanceRoot" -ForegroundColor Cyan
    } elseif (-not [string]::IsNullOrWhiteSpace($VhdxRoot)) {
        $VhdxRoot = [IO.Path]::GetFullPath($VhdxRoot)
        if (-not (Test-Path -LiteralPath $VhdxRoot -PathType Container)) { throw "-VhdxRoot is not a mounted directory: $VhdxRoot" }
        if ([string]::IsNullOrWhiteSpace($AcceptanceRoot)) { $AcceptanceRoot = $VhdxRoot }
    }

    if (-not [string]::IsNullOrWhiteSpace($AcceptanceRoot)) {
        $AcceptanceRoot = [IO.Path]::GetFullPath($AcceptanceRoot)
        if (-not (Test-Path -LiteralPath $AcceptanceRoot -PathType Container)) { throw "Acceptance root is not a directory: $AcceptanceRoot" }
        if (-not (Is-PathSafeForAcceptance $AcceptanceRoot)) { throw "Refusing to use a protected or volume-root path as the mutation root: $AcceptanceRoot" }
    }

    if ($CreateUsnJournal -and -not $CreateVhdx) {
        throw '-CreateUsnJournal is allowed only together with -CreateVhdx so the setup cannot alter an existing user volume.'
    }
    if ($CreateUsnJournal) {
        $journalSetup = Invoke-Captured 'fsutil.exe' @('usn', 'createjournal', 'm=33554432', 'a=4194304', $AcceptanceRoot)
        if ($journalSetup.ExitCode -ne 0) { throw "Explicit VHDX USN journal setup failed: $($journalSetup.Error) $($journalSetup.Output)" }
    }

    $admin = Is-Administrator
    $volume = if ($AcceptanceRoot) { Get-VolumeForPath $AcceptanceRoot } else { $null }
    $isNtfs = $null -ne $volume -and $volume.FileSystem -eq 'NTFS'
    $device = $DevicePath
    if ([string]::IsNullOrWhiteSpace($device) -and $AcceptanceRoot) {
        $driveRoot = [IO.Path]::GetPathRoot($AcceptanceRoot)
        if ($driveRoot -and $driveRoot.Length -ge 2 -and $driveRoot[1] -eq ':') { $device = "\\.\$($driveRoot[0]):" }
    }
    $vhdxAttached = $false
    $vhdxReason = 'A mounted VHDX path was not supplied.'
    if (-not [string]::IsNullOrWhiteSpace($VhdxPath)) {
        if (-not (Test-Path -LiteralPath $VhdxPath -PathType Leaf)) {
            $vhdxReason = "The VHDX does not exist: $VhdxPath"
        } elseif (-not (Get-Command Get-DiskImage -ErrorAction SilentlyContinue)) {
            $vhdxReason = 'Get-DiskImage is unavailable on this host.'
        } else {
            $image = Get-DiskImage -ImagePath $VhdxPath -ErrorAction SilentlyContinue
            $vhdxAttached = $null -ne $image -and $image.Attached -and $isNtfs
            $vhdxReason = if ($vhdxAttached) { 'Attached VHDX with an NTFS mount was detected.' } else { 'The VHDX is not attached with an NTFS mount.' }
        }
    }

    $usnReady = $false
    $usnReason = 'An NTFS device path and an existing USN journal are required.'
    if ($isNtfs -and $device -and $admin) {
        $journal = Invoke-Captured 'fsutil.exe' @('usn', 'queryjournal', $AcceptanceRoot)
        $usnReady = $journal.ExitCode -eq 0
        $usnReason = if ($usnReady) { 'The existing USN journal is queryable; the collector will not create or resize it.' } else { 'No existing USN journal was queryable; the test was not run.' }
    } elseif (-not $admin) {
        $usnReason = 'Administrator elevation is required for the USN/MFT/ETW privileged checks.'
    } elseif (-not $isNtfs) {
        $usnReason = 'The acceptance root is not on NTFS.'
    }

    $smbReady = $false
    $smbReason = 'A local SMB share name was not supplied.'
    if (-not [string]::IsNullOrWhiteSpace($SmbShareName)) {
        $share = Get-SmbShare -Name $SmbShareName -ErrorAction SilentlyContinue
        $smbReady = $null -ne $share -and $share.ContinuouslyAvailable -ne $true
        $smbReason = if ($smbReady) { 'The configured local SMB share was found.' } else { "The local SMB share was not found: $SmbShareName" }
    }

    $serviceReady = $false
    $serviceReason = 'A service name was not supplied or the service is not installed.'
    if (-not [string]::IsNullOrWhiteSpace($ServiceName)) {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        $serviceReady = $null -ne $service
        $serviceReason = if ($serviceReady) { "The service $ServiceName is installed; the test only queries SCM and does not stop or start it." } else { "The service is not installed: $ServiceName" }
    }

    $sessionReady = [Environment]::UserInteractive -and ((Get-Process -Id $PID).SessionId -gt 0)
    $sessionReason = if ($sessionReady) { 'An interactive user session is available.' } else { 'An interactive user session is unavailable; clipboard tests were not run.' }

    $removableReady = $false
    $removableReason = 'A removable-media mount root was not supplied.'
    if (-not [string]::IsNullOrWhiteSpace($RemovableRoot)) {
        $removableVolume = Get-VolumeForPath $RemovableRoot
        $removableReady = $null -ne $removableVolume -and $removableVolume.DriveType -eq 'Removable' -and $removableVolume.FileSystem -ne $null
        $removableReason = if ($removableReady) { 'A readable removable volume was detected.' } else { "The configured path is not a readable removable volume: $RemovableRoot" }
    }

    $rootReady = -not [string]::IsNullOrWhiteSpace($AcceptanceRoot) -and $null -ne $volume
    $deviceReady = -not [string]::IsNullOrWhiteSpace($device)
    $manifest.Environment = [ordered]@{
        OS = [Environment]::OSVersion.VersionString
        IsAdministrator = $admin
        AcceptanceRoot = $AcceptanceRoot
        DevicePath = $device
        VhdxPath = $VhdxPath
        VhdxRoot = $VhdxRoot
        RemovableRoot = $RemovableRoot
        SmbShareName = $SmbShareName
        ServiceName = $ServiceName
        InteractiveSession = $sessionReady
        VolumeFileSystem = if ($volume) { $volume.FileSystem } else { $null }
        VhdxAttached = $vhdxAttached
    }

    $capabilities = @(
        Add-Capability 'Vhdx' ($vhdxAttached -and $rootReady) $vhdxReason
        Add-Capability 'Usn' ($usnReady -and $deviceReady) $usnReason
        Add-Capability 'Mft' ($usnReady -and $deviceReady) $usnReason
        Add-Capability 'ReadDirectoryChangesW' $rootReady $(if ($rootReady) { 'The configured directory can be monitored by ReadDirectoryChangesW.' } else { 'A writable acceptance root is required.' })
        Add-Capability 'Etw' ($rootReady -and $admin) $(if ($rootReady -and $admin) { 'Administrator elevation and a mutation root are available for the kernel ETW session.' } else { 'Administrator elevation and a writable acceptance root are required.' })
        Add-Capability 'Smb' $smbReady $smbReason
        Add-Capability 'Service' $serviceReady $serviceReason
        Add-Capability 'Session' $sessionReady $sessionReason
        Add-Capability 'RemovableMedia' $removableReady $removableReason
        Add-Capability 'RemovableMediaEvent' ($removableReady -and $WaitForMediaChange) $(if (-not $WaitForMediaChange) { 'Media arrival/removal waiting is disarmed; pass -WaitForMediaChange only when a physical action is planned.' } elseif ($removableReady) { 'The media event wait is explicitly armed.' } else { $removableReason })
    )

    Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_ROOT' $AcceptanceRoot
    Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_DEVICE' $device
    Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_VHDX' $VhdxPath
    Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_REMOVABLE_ROOT' $RemovableRoot
    Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_SMB_SHARE' $SmbShareName
    Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_SERVICE' $ServiceName
    Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_WAIT_FOR_MEDIA' $(if ($WaitForMediaChange) { '1' } else { '0' })

    if (-not (Test-Path -LiteralPath $testProject -PathType Leaf)) { throw "Acceptance test project is missing: $testProject" }
    foreach ($capability in $capabilities) {
        if (-not $capability.Ready) {
            Add-NotExecuted $capability.Name $capability.Reason
            continue
        }

        Write-Host "RUNNING [$($capability.Name)]" -ForegroundColor Cyan
        $started = [DateTime]::UtcNow
        $testArgs = @('test', $testProject, '--configuration', $Configuration, '--', '--filter-trait', "Capability=$($capability.Name)")
        $result = Invoke-Captured 'dotnet' $testArgs
        if ($null -eq $result) { throw "The test runner returned no result for capability $($capability.Name)." }
        $testOutput = [string]$result.Output
        $testError = [string]$result.Error
        if (-not [string]::IsNullOrWhiteSpace($testOutput)) { Write-Host $testOutput.TrimEnd() }
        if (-not [string]::IsNullOrWhiteSpace($testError)) { Write-Host $testError.TrimEnd() -ForegroundColor DarkYellow }
        $status = if ($result.ExitCode -eq 0) { 'PASSED' } else { 'FAILED' }
        $manifest.Tests += [ordered]@{
            Capability = $capability.Name
            Status = $status
            ExitCode = $result.ExitCode
            DurationSeconds = ([DateTime]::UtcNow - $started).TotalSeconds
        }
        Write-Host "$status [$($capability.Name)]" -ForegroundColor $(if ($status -eq 'PASSED') { 'Green' } else { 'Red' })
        if ($result.ExitCode -ne 0) { $exitCode = 1 }
    }

    if ($manifest.Tests | Where-Object { $_.Status -eq 'NOT_EXECUTED' }) { if ($exitCode -eq 0) { $exitCode = 2 } }
    $status = if ($exitCode -eq 0) { 'PASSED' } elseif ($exitCode -eq 2) { 'NOT_EXECUTED' } else { 'FAILED' }
    Write-Manifest $exitCode $status
    Write-Host "Acceptance manifest: $manifestPath" -ForegroundColor Cyan
    Write-Host "Overall: $status (exit code $exitCode)" -ForegroundColor $(if ($exitCode -eq 0) { 'Green' } elseif ($exitCode -eq 2) { 'Yellow' } else { 'Red' })
    exit $exitCode
} catch {
    $exitCode = 1
    $manifest.Tests += [ordered]@{ Capability = 'runner'; Status = 'FAILED'; Reason = $_.Exception.Message }
    Write-Host $_.Exception.ToString() -ForegroundColor Red
    Write-Host $_.InvocationInfo.PositionMessage -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
    Write-Manifest $exitCode 'FAILED'
    exit $exitCode
} finally {
    if ($mountedVhdx -and $VhdxPath) {
        Dismount-VHD -Path $VhdxPath -ErrorAction SilentlyContinue
    }
    if ($createdVhdx -and $VhdxPath -and (Test-Path -LiteralPath $VhdxPath -PathType Leaf)) {
        Remove-Item -LiteralPath $VhdxPath -Force -ErrorAction SilentlyContinue
    }
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], 'Process') }
    Stop-Transcript | Out-Null
}
