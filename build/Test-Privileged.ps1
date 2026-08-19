[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$AcceptanceRoot,
    [string]$NonNtfsRoot,
    [string]$TestId,
    [string]$TestLabRoot,
    [string]$VhdxPath,
    [string]$VhdxRoot,
    [string]$DevicePath,
    [string]$RemovableRoot,
    [string]$SmbShareName,
    [string]$ServiceName = 'StorageChronicleAgent',
    [string]$SessionAgentExecutable,
    [string]$AgentPipeName = 'StorageChronicle.Agent',
    [switch]$CreateVhdx,
    [switch]$CreateUsnJournal,
    [switch]$WaitForMediaChange,
    [int]$MediaTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$platformTestProject = Join-Path $root 'tests/StorageChronicle.Platform.Windows.Integration.Tests/StorageChronicle.Platform.Windows.Integration.Tests.csproj'
$agentTestProject = Join-Path $root 'tests/StorageChronicle.Agent.Tests/StorageChronicle.Agent.Tests.csproj'
$testProjects = @($platformTestProject, $agentTestProject)
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
    'STORAGE_CHRONICLE_ACCEPTANCE_WAIT_FOR_MEDIA',
    'STORAGE_CHRONICLE_RECONCILIATION_EVIDENCE_PATH',
    'STORAGE_CHRONICLE_AGENT_EXE',
    'STORAGE_CHRONICLE_SESSION_AGENT_EXE',
    'STORAGE_CHRONICLE_AGENT_PIPE')
$oldEnvironment = @{}
foreach ($name in $environmentNames) { $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }

$manifest = [ordered]@{
    Schema = 'StorageChronicle.WindowsPrivilegedAcceptance.v2'
    RunId = $runId
    StartedUtc = [DateTime]::UtcNow.ToString('O')
    Configuration = $Configuration
    Status = 'RUNNING'
    AcceptanceEligible = $false
    RequiredCapabilities = @('Vhdx', 'UsnQuery', 'UsnRead', 'Mft', 'Reconciliation', 'Etw', 'ReadDirectoryChangesW', 'BufferGap', 'Smb', 'Service', 'SessionAgent', 'Clipboard', 'VolumeGuid', 'HotAttachDetach', 'AclDeniedMetadata', 'NonNtfs')
    ExitCode = $null
    Environment = [ordered]@{}
    Capabilities = @()
    Tests = @()
    Artifacts = [ordered]@{ Manifest = $manifestPath; Log = $runOutputPath }
}

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
$evidenceDirectory = Join-Path $artifactRoot "windows-privileged-$runId"
$reconciliationEvidencePath = Join-Path $evidenceDirectory 'confirmed-reconciliation.json'
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

function Assert-TestLabMarker([string]$RootPath, [string[]]$AllowedRoles) {
    $markers = @()
    foreach ($name in @('.storage-chronicle-testlab-marker.json', 'StorageChronicleTestVolume.json')) {
        $path = Join-Path $RootPath $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required TestLab marker is missing: $path" }
        $marker = Get-Content -Raw -Encoding UTF8 -LiteralPath $path | ConvertFrom-Json
        if ([string]$marker.Schema -ne 'StorageChronicle.TestLabDataMarker.v1') { throw "TestLab marker schema is invalid: $path" }
        if ($AllowedRoles -notcontains [string]$marker.Role) { throw "TestLab marker role is not allowed for this capability set: $($marker.Role)" }
        $expected = switch ([string]$marker.Role) {
            'NonNtfs' { [pscustomobject]@{ Label = 'SC_TEST_NONNTFS_VOLUME'; FileSystem = 'exFAT' } }
            'Mft' { [pscustomobject]@{ Label = 'SC_TEST_MFT_VOLUME'; FileSystem = 'NTFS' } }
            default { [pscustomobject]@{ Label = 'SC_TEST_VOLUME'; FileSystem = 'NTFS' } }
        }
        if ([string]$marker.VolumeLabel -ne $expected.Label -or [string]$marker.FileSystem -ine $expected.FileSystem) { throw "TestLab marker label/filesystem is invalid: $path" }
        if (-not [string]::IsNullOrWhiteSpace($TestId) -and [string]$marker.TestId -ne $TestId) { throw "TestLab marker TestId does not match the requested acceptance run: $path" }
        $markers += $marker
    }
    if ([string]$markers[0].Role -ne [string]$markers[1].Role -or [string]$markers[0].TestId -ne [string]$markers[1].TestId) { throw "The two TestLab markers disagree: $RootPath" }
    return $markers[0]
}

function Add-Capability([string]$Name, [bool]$Ready, [string]$Reason) {
    $manifest.Capabilities += [ordered]@{ Name = $Name; Ready = $Ready; Reason = $Reason }
    return [pscustomobject]@{ Name = $Name; Ready = $Ready; Reason = $Reason }
}

function Write-Manifest([int]$ExitCode, [string]$Status) {
    $manifest.Status = $Status
    $manifest.ExitCode = $ExitCode
    $manifest.CompletedUtc = [DateTime]::UtcNow.ToString('O')
    $requiredResults = @($manifest.Tests | Where-Object { $manifest.RequiredCapabilities -contains $_.Capability })
    $manifest.AcceptanceEligible = $Status -eq 'PASSED' -and $requiredResults.Count -eq $manifest.RequiredCapabilities.Count -and @($requiredResults | Where-Object Status -ne 'PASSED').Count -eq 0
    New-Item -ItemType Directory -Force -Path $evidenceDirectory | Out-Null
    $manifest.Artifacts.Directory = $evidenceDirectory
    $manifest.Artifacts.Environment = Join-Path $evidenceDirectory 'environment.json'
    $manifest.Artifacts.Capabilities = Join-Path $evidenceDirectory 'capabilities.json'
    $manifest.Artifacts.Oracle = Join-Path $evidenceDirectory 'oracle.json'
    $manifest.Artifacts.SourceEventSummary = Join-Path $evidenceDirectory 'source-event-summary.json'
    $manifest.Artifacts.CanonicalSummary = Join-Path $evidenceDirectory 'canonical-summary.json'
    $manifest.Artifacts.FinalStateSummary = Join-Path $evidenceDirectory 'final-state-summary.json'
    $manifest.Artifacts.ReconciliationSummary = Join-Path $evidenceDirectory 'reconciliation-summary.json'
    $manifest.Artifacts.ConfirmedReconciliation = $reconciliationEvidencePath
    $manifest.Artifacts.ServiceSummary = Join-Path $evidenceDirectory 'service-summary.json'
    $manifest.Artifacts.Errors = Join-Path $evidenceDirectory 'errors.json'
    $manifest.Artifacts.Result = Join-Path $evidenceDirectory 'result.json'
    if (-not (Test-Path -LiteralPath $manifest.Artifacts.ConfirmedReconciliation -PathType Leaf)) {
        [ordered]@{ Schema = 'StorageChronicle.ConfirmedReconciliationAcceptance.v1'; AcceptanceEligible = $false; Status = 'NOT_EXECUTED'; Reason = 'The real Agent reconciliation acceptance test did not produce evidence.' } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifest.Artifacts.ConfirmedReconciliation -Encoding UTF8
    }
    $manifest.Environment | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifest.Artifacts.Environment -Encoding UTF8
    [ordered]@{ Schema = 'StorageChronicle.WindowsPrivilegedCapabilities.v1'; Required = $manifest.RequiredCapabilities; Definitions = $manifest.Capabilities; Tests = $manifest.Tests } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifest.Artifacts.Capabilities -Encoding UTF8
    foreach ($name in @('Oracle', 'SourceEventSummary', 'CanonicalSummary', 'FinalStateSummary', 'ReconciliationSummary', 'ServiceSummary')) { [ordered]@{ Schema = "StorageChronicle.WindowsPrivileged.$name.v1"; Status = 'NOT_EXECUTED'; Reason = 'This capability artifact is populated only by the real TestLab product workload; no synthetic summary is accepted.' } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifest.Artifacts[$name] -Encoding UTF8 }
    [ordered]@{ Schema = 'StorageChronicle.WindowsPrivilegedErrors.v1'; Errors = @($manifest.Tests | Where-Object Status -in @('FAILED', 'NOT_EXECUTED')) } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifest.Artifacts.Errors -Encoding UTF8
    $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifest.Artifacts.Result -Encoding UTF8
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
}

$createdVhdx = $false
$mountedVhdx = $false
$exitCode = 0
try {
    $hostIsWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
    if (-not $hostIsWindows) {
        $manifest.Environment = [ordered]@{ OS = 'non-Windows'; Prerequisite = 'Windows is required' }
        Add-NotExecuted 'all' 'This acceptance suite requires a Windows host.'
        $exitCode = 2
        Write-Manifest $exitCode 'NOT_EXECUTED'
        exit $exitCode
    }

    $TestLabRoot = if ([string]::IsNullOrWhiteSpace($TestLabRoot)) { [Environment]::GetEnvironmentVariable('SC_TESTLAB_ROOT', 'Process') } else { $TestLabRoot }
    if ([string]::IsNullOrWhiteSpace($TestLabRoot)) {
        $manifest.Environment = [ordered]@{ Prerequisite = 'A user-approved TestLabRoot is required; privileged acceptance never targets an unbounded host path.' }
        Add-NotExecuted 'all' 'A user-approved TestLabRoot was not supplied.'
        $exitCode = 2
        Write-Manifest $exitCode 'NOT_EXECUTED'
        exit $exitCode
    }
    $TestLabRoot = [IO.Path]::GetFullPath($TestLabRoot).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $TestLabRoot -PathType Container)) { throw "The approved TestLab root does not exist: $TestLabRoot" }
    if ($CreateUsnJournal) { throw 'Refusing -CreateUsnJournal: the acceptance suite must query/read an existing journal and must not create or resize a journal.' }

    if ($CreateVhdx) {
        if ([string]::IsNullOrWhiteSpace($VhdxPath)) { throw '-CreateVhdx requires -VhdxPath.' }
        $VhdxPath = [IO.Path]::GetFullPath($VhdxPath)
        if ([IO.Path]::GetExtension($VhdxPath) -ine '.vhdx') { throw '-VhdxPath must have a .vhdx extension.' }
        if (-not ($VhdxPath.Equals($TestLabRoot, [StringComparison]::OrdinalIgnoreCase) -or $VhdxPath.StartsWith($TestLabRoot + '\', [StringComparison]::OrdinalIgnoreCase))) { throw "The disposable VHDX must be under the approved TestLab root: $VhdxPath" }
        if (Test-Path -LiteralPath $VhdxPath) { throw "Refusing to overwrite an existing VHDX: $VhdxPath" }
        if (-not (Test-Path -LiteralPath (Split-Path -Parent $VhdxPath))) { throw 'The VHDX parent directory must already exist.' }
        foreach ($command in @('New-VHD', 'Mount-VHD', 'Get-Disk', 'Initialize-Disk', 'New-Partition', 'Format-Volume')) {
            if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "The Hyper-V/Storage command $command is not available; VHDX setup was not executed." }
        }

        New-VHD -Path $VhdxPath -SizeBytes 4GB -Dynamic | Out-Null
        $createdVhdx = $true
        $diskImage = Mount-VHD -Path $VhdxPath -Passthru
        $mountedVhdx = $true
        $disk = $diskImage | Get-Disk
        if ($disk.IsOffline) { Set-Disk -Number $disk.Number -IsOffline $false }
        if ($disk.IsReadOnly) { Set-Disk -Number $disk.Number -IsReadOnly $false }
        Initialize-Disk -Number $disk.Number -PartitionStyle GPT | Out-Null
        $partition = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
        Format-Volume -Partition $partition -FileSystem NTFS -NewFileSystemLabel 'SC_TEST_VOLUME' -Confirm:$false | Out-Null
        $VhdxRoot = "$($partition.DriveLetter):\"
        $AcceptanceRoot = Join-Path $VhdxRoot 'StorageChronicleAcceptance'
        New-Item -ItemType Directory -Force -Path $AcceptanceRoot | Out-Null
        $markerValue = [ordered]@{ Schema = 'StorageChronicle.TestLabDataMarker.v1'; TestId = $runId; Role = 'Workload'; VolumeLabel = 'SC_TEST_VOLUME'; FileSystem = 'NTFS'; VhdxPath = $VhdxPath; CreatedUtc = [DateTimeOffset]::UtcNow }
        $markerJson = $markerValue | ConvertTo-Json -Depth 10
        Set-Content -LiteralPath (Join-Path $AcceptanceRoot '.storage-chronicle-testlab-marker.json') -Value $markerJson -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $AcceptanceRoot 'StorageChronicleTestVolume.json') -Value $markerJson -Encoding UTF8
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
    if (-not [string]::IsNullOrWhiteSpace($NonNtfsRoot)) {
        $NonNtfsRoot = [IO.Path]::GetFullPath($NonNtfsRoot)
        if (-not (Test-Path -LiteralPath $NonNtfsRoot -PathType Container)) { throw "Non-NTFS acceptance root is not a directory: $NonNtfsRoot" }
        if (-not (Is-PathSafeForAcceptance $NonNtfsRoot)) { throw "Refusing to use a protected or volume-root path as the non-NTFS mutation root: $NonNtfsRoot" }
    }
    $acceptanceMarker = if ($AcceptanceRoot) { Assert-TestLabMarker $AcceptanceRoot @('Workload', 'Mft', 'AclDenied') } else { $null }
    $nonNtfsMarker = if ($NonNtfsRoot) { Assert-TestLabMarker $NonNtfsRoot @('NonNtfs') } else { $null }

    $admin = Is-Administrator
    $volume = if ($AcceptanceRoot) { Get-VolumeForPath $AcceptanceRoot } else { $null }
    $isNtfs = $null -ne $volume -and $volume.FileSystem -eq 'NTFS'
    $nonNtfsVolume = if ($NonNtfsRoot) { Get-VolumeForPath $NonNtfsRoot } else { $null }
    $nonNtfsReady = $null -ne $nonNtfsVolume -and $nonNtfsVolume.FileSystem -ne 'NTFS'
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
    $sessionAgentReady = $sessionReady -and -not [string]::IsNullOrWhiteSpace($SessionAgentExecutable) -and (Test-Path -LiteralPath $SessionAgentExecutable -PathType Leaf)

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
        NonNtfsRoot = $NonNtfsRoot
        TestId = $TestId
        AcceptanceMarkerRole = if ($acceptanceMarker) { $acceptanceMarker.Role } else { $null }
        NonNtfsMarkerRole = if ($nonNtfsMarker) { $nonNtfsMarker.Role } else { $null }
        DevicePath = $device
        VhdxPath = $VhdxPath
        VhdxRoot = $VhdxRoot
        RemovableRoot = $RemovableRoot
        SmbShareName = $SmbShareName
        ServiceName = $ServiceName
        SessionAgentExecutable = $SessionAgentExecutable
        AgentPipeName = $AgentPipeName
        InteractiveSession = $sessionReady
        VolumeFileSystem = if ($volume) { $volume.FileSystem } else { $null }
        NonNtfsVolumeFileSystem = if ($nonNtfsVolume) { $nonNtfsVolume.FileSystem } else { $null }
        VhdxAttached = $vhdxAttached
        ReconciliationEvidencePath = $reconciliationEvidencePath
        TestLabRoot = $TestLabRoot
    }

    $capabilities = @(
        Add-Capability 'Vhdx' ($CreateVhdx -and $vhdxAttached -and $rootReady -and $isNtfs) $(if ($CreateVhdx) { $vhdxReason } else { 'Acceptance requires this runner to create, attach, initialize, format, detach, and destroy the disposable VHDX.' })
        Add-Capability 'UsnQuery' ($usnReady -and $deviceReady) $usnReason
        Add-Capability 'UsnRead' ($usnReady -and $deviceReady) 'The real USN query/read test must run against the existing journal without changing journal configuration.'
        Add-Capability 'Mft' ($usnReady -and $deviceReady) $usnReason
        Add-Capability 'Reconciliation' ($rootReady -and $isNtfs -and $admin) $(if ($rootReady -and $isNtfs -and $admin) { 'The real Agent reconciliation acceptance test is wired to the selected NTFS volume and requires elevation for the production MFT/metadata path.' } else { 'A real Agent reconciliation run requires an elevated Windows guest with a selected NTFS acceptance volume.' })
        Add-Capability 'Etw' ($rootReady -and $admin) $(if ($rootReady -and $admin) { 'The real kernel ETW test covers a file operation, correlated process identity, and bounded session shutdown.' } else { 'The real ETW acceptance requires an elevated acceptance root.' })
        Add-Capability 'ReadDirectoryChangesW' $rootReady $(if ($rootReady) { 'The real notification test covers create, rename, and delete and fails on a native continuity gap.' } else { 'A disposable acceptance root is required.' })
        Add-Capability 'BufferGap' $hostIsWindows 'The bounded initial-scan buffer test verifies overflow is reported instead of silently dropping notifications.'
        Add-Capability 'Smb' ($rootReady -and $admin) $(if ($rootReady -and $admin) { 'The real test creates, changes, and removes a temporary SMB share beneath the disposable acceptance root and compares actual NetShare snapshots.' } else { 'An elevated disposable acceptance root is required for the SMB lifecycle test.' })
        Add-Capability 'Service' ($rootReady -and $admin) $(if ($rootReady -and $admin) { 'The real test installs, starts, stops, queries recovery actions, and deletes a temporary Agent service.' } else { 'An elevated disposable acceptance root is required for the service lifecycle test.' })
        Add-Capability 'SessionAgent' $sessionAgentReady $(if ($sessionAgentReady) { 'The real Session Agent process is configured to wait for one actual clipboard notification and send it through the live Agent pipe.' } else { 'An interactive session, a live Agent, and an explicitly supplied Session Agent executable are required.' })
        Add-Capability 'Clipboard' $sessionReady $sessionReason
        Add-Capability 'VolumeGuid' ($rootReady -and $AcceptanceRoot -match '^\\\\\?\\Volume\{[0-9A-Fa-f-]+\}') $(if ($rootReady -and $AcceptanceRoot -match '^\\\\\?\\Volume\{[0-9A-Fa-f-]+\}') { 'The real volume enumerator test uses a drive-letter-free volume GUID root.' } else { 'The acceptance root must be supplied through a drive-letter-free volume GUID path.' })
        Add-Capability 'HotAttachDetach' ($CreateVhdx -and $vhdxAttached) $(if ($CreateVhdx -and $vhdxAttached) { 'The real test detaches and reattaches the disposable VHDX and verifies its attached state.' } else { 'The runner must create and attach the disposable VHDX before the hot attach/detach check.' })
        Add-Capability 'AclDeniedMetadata' ($rootReady -and $isNtfs -and $admin) $(if ($rootReady -and $isNtfs -and $admin) { 'The real Agent test applies an ACL deny rule and verifies the production runner reads metadata through its scoped SeBackupPrivilege path.' } else { 'An elevated NTFS acceptance root is required for the ACL-denied metadata test.' })
        Add-Capability 'NonNtfs' $nonNtfsReady $(if ($nonNtfsReady) { 'The real Agent test executes directory snapshot reconciliation on the separately selected non-NTFS volume.' } else { 'A readable non-NTFS acceptance root must be supplied with -NonNtfsRoot.' })
    )

    Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_ROOT' $AcceptanceRoot
    Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_DEVICE' $device
    Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_VHDX' $VhdxPath
    Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_REMOVABLE_ROOT' $RemovableRoot
    Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_SMB_SHARE' $SmbShareName
    Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_SERVICE' $ServiceName
    Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_WAIT_FOR_MEDIA' $(if ($WaitForMediaChange) { '1' } else { '0' })
    Set-ProcessEnvironment 'STORAGE_CHRONICLE_RECONCILIATION_EVIDENCE_PATH' $reconciliationEvidencePath
    Set-ProcessEnvironment 'STORAGE_CHRONICLE_SESSION_AGENT_EXE' $SessionAgentExecutable
    Set-ProcessEnvironment 'STORAGE_CHRONICLE_AGENT_PIPE' $AgentPipeName

    $testAssemblies = @{}
    foreach ($project in $testProjects) {
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Acceptance test project is missing: $project" }
        $projectSlug = [IO.Path]::GetFileNameWithoutExtension($project)
        & dotnet build $project --configuration $Configuration --no-restore --nologo 2>&1 | Tee-Object -FilePath (Join-Path $artifactRoot "windows-privileged-$runId.$projectSlug.build.log")
        if ($LASTEXITCODE -ne 0) { throw "Acceptance test project build failed with exit code ${LASTEXITCODE}: $project" }
        $testAssembly = Get-ChildItem (Join-Path (Split-Path -Parent $project) "bin\$Configuration") -Recurse -File -Filter ($projectSlug + '.exe') |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($null -eq $testAssembly) { throw "The MTP privileged test executable was not produced: $projectSlug" }
        $testAssemblies[[IO.Path]::GetFullPath($project)] = $testAssembly.FullName
    }
    $agentExecutable = Get-ChildItem (Join-Path $root "src\StorageChronicle.Agent\bin\$Configuration") -Recurse -File -Filter 'StorageChronicle.Agent.exe' |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $agentExecutable) { throw 'The Agent executable required by service acceptance was not produced.' }
    Set-ProcessEnvironment 'STORAGE_CHRONICLE_AGENT_EXE' $agentExecutable.FullName
    $manifest.Environment.AgentExecutable = $agentExecutable.FullName
    foreach ($capability in $capabilities) {
        if (-not $capability.Ready) {
            Add-NotExecuted $capability.Name $capability.Reason
            continue
        }

        Write-Host "RUNNING [$($capability.Name)]" -ForegroundColor Cyan
        if ($capability.Name -eq 'NonNtfs') {
            Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_ROOT' $NonNtfsRoot
            Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_DEVICE' $null
        } else {
            Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_ROOT' $AcceptanceRoot
            Set-ProcessEnvironment 'STORAGE_CHRONICLE_ACCEPTANCE_DEVICE' $device
        }
        $started = [DateTime]::UtcNow
        $testArgs = @('--progress', 'off', '--minimum-expected-tests', '1', '--filter-trait', "Capability=$($capability.Name)")
        $projectForCapability = if ($capability.Name -in @('Reconciliation', 'AclDeniedMetadata', 'NonNtfs')) { $agentTestProject } else { $platformTestProject }
        $testAssemblyPath = $testAssemblies[[IO.Path]::GetFullPath($projectForCapability)]
        if ([string]::IsNullOrWhiteSpace($testAssemblyPath)) { throw "No test executable is registered for capability $($capability.Name)." }
        $result = Invoke-Captured $testAssemblyPath $testArgs
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
