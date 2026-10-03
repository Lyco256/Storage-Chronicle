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
    [string]$WorkloadOraclePath,
    [string]$AgentHistoryPath,
    [string]$AgentPipeName = 'StorageChronicle.Agent',
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [switch]$CreateVhdx,
    [switch]$ConfirmCreateVhdx,
    [switch]$CreateUsnJournal,
    [switch]$WaitForMediaChange,
    [int]$MediaTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'quality/AcceptanceContracts.ps1')
$ErrorActionPreference = 'Stop'

function Assert-NoReparsePath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $volumeGuidRoot = [regex]::Match($fullPath, '^\\\\\?\\Volume\{[0-9A-Fa-f-]{36}\}\\', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($volumeGuidRoot.Success) {
        $rootPath = $volumeGuidRoot.Value
    } else {
        if ($fullPath.StartsWith('\\', [StringComparison]::Ordinal)) { throw "UNC/device paths are not accepted: $fullPath" }
        $rootPath = [IO.Path]::GetPathRoot($fullPath)
        if ([string]::IsNullOrWhiteSpace($rootPath) -or $rootPath -notmatch '^[A-Za-z]:\\$') { throw "A local drive or verified volume GUID path is required: $fullPath" }
    }
    $cursor = $rootPath
    if (Test-Path -LiteralPath $cursor) {
        $rootItem = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse points are not accepted in a test path: $cursor" }
    }
    $segments = $fullPath.Substring($rootPath.Length).Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)
    foreach ($segment in $segments) {
        $cursor = Join-Path $cursor $segment
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse points are not accepted in a test path: $cursor" }
        }
    }
}

function Assert-OutsideProtectedSystemRoots([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $protectedRoots = @($env:WINDIR, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData) |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        ForEach-Object { [IO.Path]::GetFullPath([string]$_).TrimEnd('\') }
    foreach ($protectedRoot in $protectedRoots) {
        if ($fullPath.Equals($protectedRoot, [StringComparison]::OrdinalIgnoreCase) -or $fullPath.StartsWith($protectedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "EvidenceRoot must not be inside a protected Windows/application data root: $protectedRoot"
        }
    }
}

function Write-NewUtf8File([string]$Path, [string]$Value) {
    $encoding = [Text.UTF8Encoding]::new($false)
    $bytes = $encoding.GetBytes($Value)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}

$platformTestProject = Join-Path $root 'tests/StorageChronicle.Platform.Windows.Integration.Tests/StorageChronicle.Platform.Windows.Integration.Tests.csproj'
$agentTestProject = Join-Path $root 'tests/StorageChronicle.Agent.Tests/StorageChronicle.Agent.Tests.csproj'
$testProjects = @($platformTestProject, $agentTestProject)
$artifactRoot = [IO.Path]::GetFullPath($EvidenceRoot)
Assert-OutsideProtectedSystemRoots $artifactRoot
$repoRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
if ($artifactRoot.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'EvidenceRoot must be outside the repository and any synced workspace.'
}
if ($artifactRoot -match '^\\\\' -or $artifactRoot -match '(?i)\\OneDrive\\|\\Documents\\') {
    throw 'EvidenceRoot must be a local non-synced path, not UNC, OneDrive, or Documents.'
}
if (-not (Test-Path -LiteralPath (Split-Path -Parent $artifactRoot) -PathType Container)) { throw 'EvidenceRoot parent directory must already exist.' }
Assert-NoReparsePath (Split-Path -Parent $artifactRoot)
if (Test-Path -LiteralPath $artifactRoot) {
    throw "EvidenceRoot must be a new path; refusing to write into existing data: $artifactRoot"
}
$evidenceVolume = Get-Volume -FilePath (Split-Path -Parent $artifactRoot) -ErrorAction Stop
if ([string]$evidenceVolume.FileSystem -ne 'NTFS' -or [string]$evidenceVolume.DriveType -ne 'Fixed') { throw 'EvidenceRoot must be on a local fixed NTFS volume.' }
$runId = if ([string]::IsNullOrWhiteSpace($TestId)) { [guid]::NewGuid().ToString('D') } else { $TestId }
$parsedRunId = [guid]::Empty
if (-not [guid]::TryParse($runId, [ref]$parsedRunId)) { throw 'TestId must be a GUID for privileged acceptance.' }
$runId = $parsedRunId.ToString('D')
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
    RequiredCapabilities = @(Get-RequiredWindowsPrivilegedCapabilities)
    ExitCode = $null
    Environment = [ordered]@{}
    Capabilities = @()
    Tests = @()
    Artifacts = [ordered]@{ Manifest = $manifestPath; Log = $runOutputPath }
}

New-Item -ItemType Directory -Path $artifactRoot | Out-Null
$evidenceDirectory = Join-Path $artifactRoot "windows-privileged-$runId"
$reconciliationEvidencePath = Join-Path $evidenceDirectory 'confirmed-reconciliation.json'
Start-Transcript -Path $runOutputPath -NoClobber | Out-Null

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

function Invoke-DiskPartScript([string]$Root, [string[]]$Lines) {
    $scriptPath = Join-Path $Root ('.storage-chronicle-diskpart-' + $runId + '.txt')
    if (-not ([IO.Path]::GetFullPath($scriptPath).StartsWith([IO.Path]::GetFullPath($Root).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase))) { throw 'The DiskPart script path escaped TestLabRoot.' }
    try {
        $bytes = [Text.Encoding]::ASCII.GetBytes(($Lines -join [Environment]::NewLine))
        $stream = [IO.File]::Open($scriptPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        $result = Invoke-Captured 'diskpart.exe' @('/s', $scriptPath)
        if ($result.ExitCode -ne 0) { throw "DiskPart failed with exit code $($result.ExitCode): $($result.Error.Trim())" }
        return $result
    } finally {
        if (Test-Path -LiteralPath $scriptPath) { Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue }
    }
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
        if ($root -match '^\\\\\?\\Volume\{[0-9A-Fa-f-]{36}\}\\$') {
            $escapedDeviceId = $root.Replace('\', '\\').Replace("'", "''")
            $volumeMatches = @(Get-CimInstance -ClassName Win32_Volume -Filter "DeviceID='$escapedDeviceId'" -ErrorAction Stop)
            if ($volumeMatches.Count -eq 1 -and [string]$volumeMatches[0].DeviceID -ieq $root) {
                return [pscustomobject]@{
                    UniqueId = [string]$volumeMatches[0].DeviceID
                    FileSystem = [string]$volumeMatches[0].FileSystem
                    FileSystemLabel = [string]$volumeMatches[0].Label
                    DriveType = if ([int]$volumeMatches[0].DriveType -eq 3) { 'Fixed' } elseif ([int]$volumeMatches[0].DriveType -eq 2) { 'Removable' } else { 'Unknown' }
                    SizeRemaining = [uint64]$volumeMatches[0].FreeSpace
                }
            }
        }
    } catch {
        return $null
    }

    return $null
}

function Get-VerifiedVhdxDisk([string]$ExpectedUniqueId, [switch]$RequireRawUnpartitioned) {
    $image = Get-DiskImage -ImagePath $VhdxPath -ErrorAction Stop
    $actualImagePath = [IO.Path]::GetFullPath([string]$image.ImagePath)
    if (-not $image.Attached -or -not $actualImagePath.Equals($VhdxPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The attached disk image does not resolve to the exact newly created VHDX path.'
    }
    $disks = @($image | Get-Disk -ErrorAction Stop)
    if ($disks.Count -ne 1) { throw "Expected exactly one disk for the VHDX image; found $($disks.Count)." }
    $disk = $disks[0]
    $uniqueId = [string]$disk.UniqueId
    if ([string]::IsNullOrWhiteSpace($uniqueId)) { throw 'The attached VHDX disk has no stable unique ID.' }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedUniqueId) -and -not $uniqueId.Equals($ExpectedUniqueId, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The VHDX disk unique ID changed during the operation.'
    }
    if ([string]$disk.BusType -ne 'File Backed Virtual') { throw "The image is not reported as a file-backed virtual disk: $($disk.BusType)" }
    if ($null -eq $disk.IsBoot -or $null -eq $disk.IsSystem) { throw 'System/boot classification is unavailable; disk mutation is refused.' }
    if ($disk.IsBoot -or $disk.IsSystem) { throw 'The image disk is classified as a boot or system disk.' }
    if ($disk.Size -lt 4GB) { throw 'The attached VHDX is smaller than the required 4 GiB test volume.' }
    # Storage cmdlets below resolve disks again by UniqueId. Verify that lookup
    # still denotes the exact disk returned for this image on every guard call.
    $resolvedDisks = @(Get-Disk -UniqueId $uniqueId -ErrorAction Stop)
    if ($resolvedDisks.Count -ne 1 -or [string]$resolvedDisks[0].UniqueId -ne $uniqueId -or [uint32]$resolvedDisks[0].Number -ne [uint32]$disk.Number -or [string]$resolvedDisks[0].BusType -ne 'File Backed Virtual') {
        throw 'UniqueId no longer resolves to exactly the file-backed disk mapped from the approved VHDX image.'
    }
    $resolvedImage = Get-DiskImage -ImagePath $VhdxPath -ErrorAction Stop
    $resolvedImagePath = [IO.Path]::GetFullPath([string]$resolvedImage.ImagePath)
    $resolvedImageDisks = @($resolvedImage | Get-Disk -ErrorAction Stop)
    if (-not $resolvedImage.Attached -or -not $resolvedImagePath.Equals($VhdxPath, [StringComparison]::OrdinalIgnoreCase) -or $resolvedImageDisks.Count -ne 1 -or [string]$resolvedImageDisks[0].UniqueId -ne $uniqueId -or [uint32]$resolvedImageDisks[0].Number -ne [uint32]$disk.Number) {
        throw 'The disk identity no longer maps to the exact approved attached VHDX image.'
    }
    if ($RequireRawUnpartitioned) {
        $partitions = @(Get-Partition -DiskId $uniqueId -ErrorAction Stop)
        if ([string]$disk.PartitionStyle -ne 'RAW' -or $partitions.Count -ne 0) {
            throw 'The newly created VHDX is not a blank RAW disk; initialization and formatting are refused.'
        }
    }
    return $disk
}

function Resolve-ExistingVolumeForConfiguredPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $candidate = [Environment]::ExpandEnvironmentVariables($Path.Trim())
    $candidate = $candidate.Replace('\\?\', '').Replace('\??\', '')
    $probe = $candidate
    while (-not (Test-Path -LiteralPath $probe)) {
        $parent = Split-Path -Parent $probe
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $probe) { throw "Could not resolve configured OS-role path to an existing volume: $candidate" }
        $probe = $parent
    }
    $volume = Get-Volume -FilePath $probe -ErrorAction Stop
    if ($null -eq $volume -or [string]::IsNullOrWhiteSpace([string]$volume.UniqueId)) { throw "Could not resolve an OS-role path to one stable volume identity: $candidate" }
    return $volume
}

function Assert-VolumePathIsNotOnVhdxDisk([string]$Path, [string]$DiskUniqueId) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $volume = Resolve-ExistingVolumeForConfiguredPath $Path
    $partitions = @($volume | Get-Partition -ErrorAction Stop)
    $disks = @($partitions | Get-Disk -ErrorAction Stop)
    if (@($disks | Where-Object { [string]$_.UniqueId -eq $DiskUniqueId }).Count -gt 0) {
        throw "The VHDX disk is configured as a pagefile or crash-dump target: $candidate"
    }
}

function Assert-VhdxDiskHasNoPagingOrCrashDumpRole([string]$DiskUniqueId) {
    $pageFiles = @(Get-CimInstance -ClassName Win32_PageFileUsage -ErrorAction Stop)
    foreach ($pageFile in $pageFiles) { Assert-VolumePathIsNotOnVhdxDisk ([string]$pageFile.Name) $DiskUniqueId }
    $crashControl = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' -ErrorAction Stop
    foreach ($propertyName in @('DumpFile', 'DedicatedDumpFile', 'MinidumpDir')) {
        Assert-VolumePathIsNotOnVhdxDisk ([string]$crashControl.$propertyName) $DiskUniqueId
    }
}

function Assert-VhdxVolumeHasNoPagingOrCrashDumpRole([string]$DiskUniqueId, [string]$VolumeUniqueId) {
    if ([string]::IsNullOrWhiteSpace($VolumeUniqueId)) { throw 'The VHDX volume has no stable identity for pagefile/crash-dump role checks.' }
    $pageFiles = @(Get-CimInstance -ClassName Win32_PageFileUsage -ErrorAction Stop)
    foreach ($pageFile in $pageFiles) {
        $candidate = [Environment]::ExpandEnvironmentVariables(([string]$pageFile.Name).Trim()).Replace('\\?\', '').Replace('\??\', '')
        $pageVolume = Resolve-ExistingVolumeForConfiguredPath $candidate
        if ([string]$pageVolume.UniqueId -eq $VolumeUniqueId) { throw 'The VHDX volume is used for a pagefile; formatting is refused.' }
        Assert-VolumePathIsNotOnVhdxDisk $candidate $DiskUniqueId
    }
    $crashControl = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' -ErrorAction Stop
    foreach ($propertyName in @('DumpFile', 'DedicatedDumpFile', 'MinidumpDir')) {
        $value = [Environment]::ExpandEnvironmentVariables(([string]$crashControl.$propertyName).Trim()).Replace('\\?\', '').Replace('\??\', '')
        if ([string]::IsNullOrWhiteSpace($value)) { continue }
        $dumpVolume = Resolve-ExistingVolumeForConfiguredPath $value
        if ([string]$dumpVolume.UniqueId -eq $VolumeUniqueId) { throw 'The VHDX volume is configured for crash dumps; formatting is refused.' }
        Assert-VolumePathIsNotOnVhdxDisk $value $DiskUniqueId
    }
}

function Is-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Is-PathSafeForAcceptance([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($fullPath.Length -lt 4) { return $false }
    $pathRoot = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Path)).TrimEnd('\')
    if ($fullPath.Equals($pathRoot, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    $forbidden = @(
        [IO.Path]::GetFullPath($env:WINDIR).TrimEnd('\'),
        [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\'),
        [IO.Path]::GetFullPath(${env:ProgramFiles(x86)}).TrimEnd('\'))
    $forbidden += [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\')
    foreach ($item in $forbidden) {
        if ($fullPath.Equals($item, [StringComparison]::OrdinalIgnoreCase) -or $fullPath.StartsWith($item + '\', [StringComparison]::OrdinalIgnoreCase)) { return $false }
    }
    if ($fullPath -match '(?i)(^|\\)(OneDrive|Documents)(\\|$)') { return $false }
    return $true
}

function Assert-TestLabMarker([string]$RootPath, [string[]]$AllowedRoles) {
    Assert-NoReparsePath $RootPath
    if (-not (Is-PathSafeForAcceptance $RootPath)) { throw "The marked fixture root is inside a protected or synchronized path: $RootPath" }
    $volume = Get-VolumeForPath $RootPath
    if ($null -eq $volume -or [string]::IsNullOrWhiteSpace([string]$volume.UniqueId)) { throw "The marked fixture root has no authoritative volume identity: $RootPath" }
    $allowedEntries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void]$allowedEntries.Add('.storage-chronicle-testlab-marker.json')
    [void]$allowedEntries.Add('StorageChronicleTestVolume.json')
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($RootPath)) {
        Assert-NoReparsePath $entry
        if (-not $allowedEntries.Remove([IO.Path]::GetFileName($entry))) { throw "Fixture root is not fresh/run-owned; refusing an unknown or duplicate entry: $entry" }
        if ([IO.Directory]::Exists($entry)) { throw "A fixture ownership marker is not a regular file: $entry" }
    }
    if ($allowedEntries.Count -ne 0) { throw "The fixture root is missing an ownership marker: $RootPath" }

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
        if ([string]$marker.VolumeLabel -cne $expected.Label -or [string]$marker.FileSystem -ine $expected.FileSystem) { throw "TestLab marker label/filesystem is invalid: $path" }
        if ([string]$marker.TestId -ne $runId) { throw "TestLab marker TestId does not match this unique acceptance run: $path" }
        if ([string]::IsNullOrWhiteSpace([string]$marker.VolumeUniqueId) -or [string]$marker.VolumeUniqueId -ne [string]$volume.UniqueId) { throw "TestLab marker volume identity does not match the volume resolved from the fixture root: $path" }
        if ([string]$volume.FileSystem -ine [string]$marker.FileSystem -or [string]$volume.FileSystemLabel -cne [string]$marker.VolumeLabel) { throw "The live volume filesystem or label does not match its run marker: $path" }
        $markers += $marker
    }
    if ([string]$markers[0].Role -ne [string]$markers[1].Role -or [string]$markers[0].TestId -ne [string]$markers[1].TestId -or [string]$markers[0].VolumeUniqueId -ne [string]$markers[1].VolumeUniqueId) { throw "The two TestLab markers disagree: $RootPath" }
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
    $manifest.Artifacts.RealIoOracle = Join-Path $evidenceDirectory 'real-io-evidence.json'
    if (-not (Test-Path -LiteralPath $manifest.Artifacts.ConfirmedReconciliation -PathType Leaf)) {
        [ordered]@{ Schema = 'StorageChronicle.ConfirmedReconciliationAcceptance.v1'; AcceptanceEligible = $false; Status = 'NOT_EXECUTED'; Reason = 'The real Agent reconciliation acceptance test did not produce evidence.' } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifest.Artifacts.ConfirmedReconciliation -Encoding UTF8
    }
    $manifest.Environment | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifest.Artifacts.Environment -Encoding UTF8
    [ordered]@{ Schema = 'StorageChronicle.WindowsPrivilegedCapabilities.v1'; Required = $manifest.RequiredCapabilities; Definitions = $manifest.Capabilities; Tests = $manifest.Tests } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifest.Artifacts.Capabilities -Encoding UTF8
    foreach ($name in @('Oracle', 'SourceEventSummary', 'CanonicalSummary', 'FinalStateSummary', 'ReconciliationSummary', 'ServiceSummary')) { [ordered]@{ Schema = "StorageChronicle.WindowsPrivileged.$name.v1"; Status = 'NOT_EXECUTED'; Reason = 'This capability artifact is populated only by the real TestLab product workload; no synthetic summary is accepted.' } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifest.Artifacts[$name] -Encoding UTF8 }
    $productEvidenceReady = Populate-ProductEvidence
    $manifest.ProductEvidenceReady = $productEvidenceReady
    if (-not $productEvidenceReady -and $Status -eq 'PASSED') {
        $Status = 'NOT_EXECUTED'
        if ($ExitCode -eq 0) { $ExitCode = 2 }
    }
    $manifest.Status = $Status
    $manifest.ExitCode = $ExitCode
    $manifest.AcceptanceEligible = $Status -eq 'PASSED' -and $productEvidenceReady -and $requiredResults.Count -eq $manifest.RequiredCapabilities.Count -and @($requiredResults | Where-Object Status -ne 'PASSED').Count -eq 0
    $errors = @($manifest.Tests | Where-Object Status -in @('FAILED', 'NOT_EXECUTED'))
    if ($manifest.Contains('ProductEvidenceError') -and -not [string]::IsNullOrWhiteSpace([string]$manifest.ProductEvidenceError)) { $errors += [ordered]@{ Capability = 'ProductEvidence'; Status = 'NOT_EXECUTED'; Reason = [string]$manifest.ProductEvidenceError } }
    [ordered]@{ Schema = 'StorageChronicle.WindowsPrivilegedErrors.v1'; Errors = $errors } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifest.Artifacts.Errors -Encoding UTF8
    $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifest.Artifacts.Result -Encoding UTF8
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
}

function Save-FinalManifest {
    $json = $manifest | ConvertTo-Json -Depth 20
    Set-Content -LiteralPath $manifestPath -Value $json -Encoding UTF8
    if ($manifest.Artifacts.Contains('Result') -and (Test-Path -LiteralPath ([string]$manifest.Artifacts.Result) -PathType Leaf)) {
        Set-Content -LiteralPath ([string]$manifest.Artifacts.Result) -Value $json -Encoding UTF8
    }
}

function Write-ProductEvidencePayload([string]$Name, $Value) {
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifest.Artifacts[$Name] -Encoding UTF8
}

function Populate-ProductEvidence {
    if ([string]::IsNullOrWhiteSpace($WorkloadOraclePath) -or [string]::IsNullOrWhiteSpace($AgentHistoryPath)) {
        $manifest.ProductEvidenceError = 'A real workload oracle and Agent history directory are required; capability-only output remains ineligible.'
        return $false
    }
    try {
        $oracleFull = [IO.Path]::GetFullPath($WorkloadOraclePath)
        $historyFull = [IO.Path]::GetFullPath($AgentHistoryPath)
        if (-not (Test-Path -LiteralPath $oracleFull -PathType Leaf)) { throw "The real workload oracle does not exist: $oracleFull" }
        if (-not (Test-Path -LiteralPath $historyFull -PathType Container)) { throw "The real Agent history directory does not exist: $historyFull" }
        if (-not [string]::Equals([IO.Path]::GetDirectoryName($oracleFull), $AcceptanceRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'The workload oracle must be a direct child of the validated TestLab acceptance root.' }
        if ([IO.Path]::GetFileName($oracleFull) -notmatch '^oracle-[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\.json$') { throw 'The workload oracle must use the run-bound oracle-{guid}.json filename.' }
        $oracleDocument = Get-Content -Raw -Encoding UTF8 -LiteralPath $oracleFull | ConvertFrom-Json
        if ([string]$oracleDocument.Schema -ne 'StorageChronicle.FileMutationWorkload.v2' -or [string]$oracleDocument.RunId -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' -or [IO.Path]::GetFileName($oracleFull) -ne "oracle-$($oracleDocument.RunId).json") { throw 'The workload oracle schema, GUID, and run-bound filename do not agree.' }
        if ($null -eq $acceptanceMarker -or [string]$oracleDocument.RunId -ne [string]$acceptanceMarker.TestId) { throw 'The workload oracle RunId does not match the approved TestLab root marker.' }
        $validatorOutputPath = Join-Path (Split-Path -Parent $oracleFull) "real-io-evidence-$($oracleDocument.RunId).json"
        $validatorProject = Join-Path $root 'tools/StorageChronicle.RealIoOracleValidator/StorageChronicle.RealIoOracleValidator.csproj'
        if (-not (Test-Path -LiteralPath $validatorProject -PathType Leaf)) { throw "The real-I/O validator project is missing: $validatorProject" }
        & dotnet run --project $validatorProject --configuration Release --no-restore -- --oracle $oracleFull --history $historyFull --output $validatorOutputPath 2>&1 | Tee-Object -FilePath (Join-Path $artifactRoot "windows-privileged-$runId.real-io-validator.log")
        if ($LASTEXITCODE -ne 0) { throw "The real-I/O validator failed with exit code $LASTEXITCODE." }
        $validatorEvidenceJson = [IO.File]::ReadAllText($validatorOutputPath, [Text.Encoding]::UTF8)
        $realIo = $validatorEvidenceJson | ConvertFrom-Json
        if ([string]$realIo.Schema -ne 'StorageChronicle.WindowsTestLabRealIoEvidence.v1' -or [string]$realIo.Status -ne 'PASSED' -or -not [bool]$realIo.AcceptanceEligible -or [int64]$realIo.OracleOperationCount -le 0 -or [int64]$realIo.SourceEventCount -le 0 -or [int64]$realIo.CanonicalEventCount -le 0 -or [int64]$realIo.FinalStateCount -le 0 -or @($realIo.FailureReasons).Count -ne 0 -or @($realIo.Checks | Where-Object Status -ne 'PASSED').Count -ne 0) { throw 'The real-I/O evidence is incomplete, failed, or ineligible.' }
        Write-NewUtf8File ([string]$manifest.Artifacts.RealIoOracle) $validatorEvidenceJson
        $base = [ordered]@{ Status = 'PASSED'; AcceptanceEligible = $true; OraclePath = $oracleFull; HistoryPath = $historyFull; RunId = [string]$realIo.RunId; Scenario = [string]$realIo.Scenario; OracleOperationCount = [int64]$realIo.OracleOperationCount; SourceEventCount = [int64]$realIo.SourceEventCount; CanonicalEventCount = [int64]$realIo.CanonicalEventCount; FinalStateCount = [int64]$realIo.FinalStateCount; Checks = @($realIo.Checks); FailureReasons = @() }
        Write-ProductEvidencePayload 'Oracle' ([ordered]@{ Schema = 'StorageChronicle.WindowsPrivileged.Oracle.v1'; Status = $base.Status; AcceptanceEligible = $base.AcceptanceEligible; OraclePath = $base.OraclePath; RunId = $base.RunId; Scenario = $base.Scenario; OracleOperationCount = $base.OracleOperationCount })
        Write-ProductEvidencePayload 'SourceEventSummary' ([ordered]@{ Schema = 'StorageChronicle.WindowsPrivileged.SourceEventSummary.v1'; Status = $base.Status; AcceptanceEligible = $base.AcceptanceEligible; SourceEventCount = $base.SourceEventCount; Checks = $base.Checks; HistoryPath = $base.HistoryPath })
        Write-ProductEvidencePayload 'CanonicalSummary' ([ordered]@{ Schema = 'StorageChronicle.WindowsPrivileged.CanonicalSummary.v1'; Status = $base.Status; AcceptanceEligible = $base.AcceptanceEligible; CanonicalEventCount = $base.CanonicalEventCount; Checks = $base.Checks; HistoryPath = $base.HistoryPath })
        Write-ProductEvidencePayload 'FinalStateSummary' ([ordered]@{ Schema = 'StorageChronicle.WindowsPrivileged.FinalStateSummary.v1'; Status = $base.Status; AcceptanceEligible = $base.AcceptanceEligible; FinalStateCount = $base.FinalStateCount; Checks = $base.Checks; HistoryPath = $base.HistoryPath })
        $serviceTest = @($manifest.Tests | Where-Object { [string]$_.Capability -eq 'Service' -and [string]$_.Status -eq 'PASSED' })
        if ($serviceTest.Count -ne 1) { throw 'The Service capability did not produce exactly one passed result.' }
        Write-ProductEvidencePayload 'ServiceSummary' ([ordered]@{ Schema = 'StorageChronicle.WindowsPrivileged.ServiceSummary.v1'; Status = 'PASSED'; AcceptanceEligible = $true; Capability = 'Service'; Test = $serviceTest[0]; EvidencePath = $runOutputPath })
        if (-not (Test-Path -LiteralPath $reconciliationEvidencePath -PathType Leaf)) { throw "The confirmed reconciliation evidence was not produced: $reconciliationEvidencePath" }
        $reconciliation = Get-Content -Raw -Encoding UTF8 -LiteralPath $reconciliationEvidencePath | ConvertFrom-Json
        if ([string]$reconciliation.Schema -ne 'StorageChronicle.ConfirmedReconciliationAcceptance.v1' -or [string]$reconciliation.Status -ne 'PASSED' -or -not [bool]$reconciliation.AcceptanceEligible) { throw 'Confirmed reconciliation evidence is not an eligible passed result.' }
        Write-ProductEvidencePayload 'ReconciliationSummary' ([ordered]@{ Schema = 'StorageChronicle.WindowsPrivileged.ReconciliationSummary.v1'; Status = 'PASSED'; AcceptanceEligible = $true; EvidencePath = $reconciliationEvidencePath; RunId = [string]$reconciliation.RunId; SourceEventCount = [int64]$reconciliation.SourceEventCount; CanonicalEventCount = [int64]$reconciliation.CanonicalEventCount; FinalStateCount = [int64]$reconciliation.FinalStateCount; CandidateCount = [int64]$reconciliation.CandidateCount; DetailedMetadataQueryCount = [int64]$reconciliation.DetailedMetadataQueryCount })
        return $true
    } catch {
        $manifest.ProductEvidenceError = $_.Exception.Message
        return $false
    }
}

$createdVhdx = $false
$createdVhdxDiskUniqueId = $null
$createdVhdxVolumeUniqueId = $null
$detachFailure = $null
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
    $normalizedTestLabRoot = [IO.Path]::GetFullPath($TestLabRoot)
    $volumeRoot = [IO.Path]::GetPathRoot($normalizedTestLabRoot)
    if ($normalizedTestLabRoot.TrimEnd('\').Equals($volumeRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'TestLabRoot cannot be a volume root.'
    }
    $TestLabRoot = $normalizedTestLabRoot.TrimEnd('\')
    if (-not (Test-Path -LiteralPath $TestLabRoot -PathType Container)) { throw "The approved TestLab root does not exist: $TestLabRoot" }
    if ($TestLabRoot.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or $TestLabRoot -match '(?i)\\OneDrive\\|\\Documents\\') {
        throw 'TestLabRoot must be outside the repository, OneDrive, and Documents.'
    }
    if ($TestLabRoot.Equals($artifactRoot, [StringComparison]::OrdinalIgnoreCase) -or $TestLabRoot.StartsWith($artifactRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $artifactRoot.StartsWith($TestLabRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'EvidenceRoot and TestLabRoot must be separate, non-overlapping directories.'
    }
    if (-not (Is-PathSafeForAcceptance $TestLabRoot)) { throw "Refusing a volume root or protected TestLabRoot: $TestLabRoot" }
    Assert-NoReparsePath $TestLabRoot
    $testLabVolume = Get-VolumeForPath $TestLabRoot
    if ($null -eq $testLabVolume -or [string]$testLabVolume.FileSystem -ne 'NTFS' -or [string]$testLabVolume.DriveType -ne 'Fixed') {
        throw 'TestLabRoot must resolve to a local fixed NTFS volume.'
    }
    if ([uint64]$testLabVolume.SizeRemaining -lt 40GB) { throw 'At least 40 GiB free space is required before creating a new VHDX.' }
    if ($CreateUsnJournal) { throw 'Refusing -CreateUsnJournal: the acceptance suite must query/read an existing journal and must not create or resize a journal.' }

    if ($CreateVhdx) {
        if (-not $ConfirmCreateVhdx) { throw 'VHDX creation requires explicit -ConfirmCreateVhdx authorization after the physical safety review.' }
        if ([string]::IsNullOrWhiteSpace($TestId)) { throw 'VHDX creation requires an explicit unique TestId GUID included in the VHDX filename.' }
        if ([string]::IsNullOrWhiteSpace($VhdxPath)) { throw '-CreateVhdx requires -VhdxPath.' }
        $VhdxPath = [IO.Path]::GetFullPath($VhdxPath)
        if ([IO.Path]::GetExtension($VhdxPath) -ine '.vhdx') { throw '-VhdxPath must have a .vhdx extension.' }
        if ([IO.Path]::GetFileName($VhdxPath).IndexOf($runId, [StringComparison]::OrdinalIgnoreCase) -lt 0) { throw 'The new VHDX filename must include the current TestId GUID.' }
        if (-not ($VhdxPath.Equals($TestLabRoot, [StringComparison]::OrdinalIgnoreCase) -or $VhdxPath.StartsWith($TestLabRoot + '\', [StringComparison]::OrdinalIgnoreCase))) { throw "The disposable VHDX must be under the approved TestLab root: $VhdxPath" }
        if (Test-Path -LiteralPath $VhdxPath) { throw "Refusing to overwrite an existing VHDX: $VhdxPath" }
        if (-not (Test-Path -LiteralPath (Split-Path -Parent $VhdxPath))) { throw 'The VHDX parent directory must already exist.' }
        Assert-NoReparsePath (Split-Path -Parent $VhdxPath)
        $vhdxParentVolume = Get-VolumeForPath (Split-Path -Parent $VhdxPath)
        if ($null -eq $vhdxParentVolume -or [string]$vhdxParentVolume.UniqueId -ne [string]$testLabVolume.UniqueId) {
            throw 'The VHDX parent directory is not on the exact verified TestLabRoot volume.'
        }
        if ([uint64]$vhdxParentVolume.SizeRemaining -lt 40GB) { throw 'At least 40 GiB free space is required immediately before VHDX creation.' }
        if (-not (Is-Administrator)) { throw 'Creating or formatting a VHDX requires an explicitly elevated acceptance process.' }
        foreach ($command in @('Get-DiskImage', 'Mount-DiskImage', 'Dismount-DiskImage', 'Get-Disk', 'Set-Disk', 'Initialize-Disk', 'New-Partition', 'Format-Volume')) {
            if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "The Windows Storage command $command is not available; VHDX setup was not executed." }
        }

        $createdVhdx = $true
        Invoke-DiskPartScript -Root $TestLabRoot -Lines @(
            ('create vdisk file="{0}" maximum=4096 type=expandable' -f $VhdxPath),
            ('select vdisk file="{0}"' -f $VhdxPath),
            'attach vdisk'
        ) | Out-Null
        $disk = Get-VerifiedVhdxDisk '' -RequireRawUnpartitioned
        $diskUniqueId = [string]$disk.UniqueId
        Assert-VhdxDiskHasNoPagingOrCrashDumpRole $diskUniqueId
        if ($disk.IsOffline) {
            $disk = Get-VerifiedVhdxDisk $diskUniqueId -RequireRawUnpartitioned
            Assert-VhdxDiskHasNoPagingOrCrashDumpRole $diskUniqueId
            Set-Disk -UniqueId $diskUniqueId -IsOffline $false -ErrorAction Stop
        }
        $disk = Get-VerifiedVhdxDisk $diskUniqueId -RequireRawUnpartitioned
        if ($disk.IsReadOnly) {
            Assert-VhdxDiskHasNoPagingOrCrashDumpRole $diskUniqueId
            Set-Disk -UniqueId $diskUniqueId -IsReadOnly $false -ErrorAction Stop
        }
        $disk = Get-VerifiedVhdxDisk $diskUniqueId -RequireRawUnpartitioned
        Assert-VhdxDiskHasNoPagingOrCrashDumpRole $diskUniqueId
        $disk = Get-VerifiedVhdxDisk $diskUniqueId -RequireRawUnpartitioned
        Initialize-Disk -UniqueId $diskUniqueId -PartitionStyle GPT -Confirm:$false -ErrorAction Stop | Out-Null
        $disk = Get-VerifiedVhdxDisk $diskUniqueId
        Assert-VhdxDiskHasNoPagingOrCrashDumpRole $diskUniqueId
        $disk = Get-VerifiedVhdxDisk $diskUniqueId
        $partition = New-Partition -DiskId $diskUniqueId -UseMaximumSize -AssignDriveLetter -ErrorAction Stop
        $partitions = @(Get-Partition -DiskId $diskUniqueId -ErrorAction Stop)
        if ($partitions.Count -ne 1 -or [string]$partitions[0].DiskId -ne $diskUniqueId -or [uint64]$partitions[0].Size -le 0) {
            throw 'The newly created partition does not uniquely match the verified VHDX disk.'
        }
        $partition = $partitions[0]
        $disk = Get-VerifiedVhdxDisk $diskUniqueId
        $partitions = @(Get-Partition -DiskId $diskUniqueId -ErrorAction Stop)
        if ($partitions.Count -ne 1 -or [string]$partitions[0].DiskId -ne $diskUniqueId -or [uint64]$partitions[0].Offset -ne [uint64]$partition.Offset) {
            throw 'The partition identity changed before formatting; format is refused.'
        }
        $unformattedVolume = Get-Volume -Partition $partitions[0] -ErrorAction Stop
        Assert-VhdxVolumeHasNoPagingOrCrashDumpRole $diskUniqueId ([string]$unformattedVolume.UniqueId)
        $disk = Get-VerifiedVhdxDisk $diskUniqueId
        $partitions = @(Get-Partition -DiskId $diskUniqueId -ErrorAction Stop)
        if ($partitions.Count -ne 1 -or [string]$partitions[0].DiskId -ne $diskUniqueId -or [uint64]$partitions[0].Offset -ne [uint64]$partition.Offset) {
            throw 'The verified VHDX partition changed immediately before formatting; format is refused.'
        }
        $currentVolume = Get-Volume -Partition $partitions[0] -ErrorAction Stop
        if ([string]$currentVolume.UniqueId -ne [string]$unformattedVolume.UniqueId -or -not [string]::IsNullOrWhiteSpace([string]$currentVolume.FileSystem)) {
            throw 'The volume identity or blank-filesystem state changed immediately before formatting.'
        }
        Assert-VhdxVolumeHasNoPagingOrCrashDumpRole $diskUniqueId ([string]$currentVolume.UniqueId)
        $disk = Get-VerifiedVhdxDisk $diskUniqueId
        Format-Volume -Partition $partitions[0] -FileSystem NTFS -NewFileSystemLabel 'SC_TEST_VOLUME' -Confirm:$false -ErrorAction Stop | Out-Null
        $volume = Get-Volume -Partition $partitions[0] -ErrorAction Stop
        if ($null -eq $volume -or [string]$volume.FileSystem -ne 'NTFS' -or [string]$volume.FileSystemLabel -ne 'SC_TEST_VOLUME' -or [string]::IsNullOrWhiteSpace([string]$volume.UniqueId)) {
            throw 'The formatted volume identity/label does not match the verified disposable VHDX.'
        }
        if ([string]$volume.UniqueId -notmatch '^\\\\\?\\Volume\{[0-9A-Fa-f-]+\}\\$') { throw 'The formatted VHDX volume has no valid Volume GUID path.' }
        $createdVhdxDiskUniqueId = $diskUniqueId
        $createdVhdxVolumeUniqueId = [string]$volume.UniqueId
        $VhdxRoot = [string]$volume.UniqueId
        $AcceptanceRoot = Join-Path $VhdxRoot 'StorageChronicleAcceptance'
        if (Test-Path -LiteralPath $AcceptanceRoot) { throw 'The freshly formatted VHDX unexpectedly contains an existing acceptance directory.' }
        New-Item -ItemType Directory -Path $AcceptanceRoot | Out-Null
        $markerValue = [ordered]@{ Schema = 'StorageChronicle.TestLabDataMarker.v1'; TestId = $runId; Role = 'Workload'; VolumeLabel = 'SC_TEST_VOLUME'; FileSystem = 'NTFS'; VhdxPath = $VhdxPath; DiskUniqueId = $createdVhdxDiskUniqueId; VolumeUniqueId = $createdVhdxVolumeUniqueId; CreatedUtc = [DateTimeOffset]::UtcNow }
        $markerJson = $markerValue | ConvertTo-Json -Depth 10
        Write-NewUtf8File (Join-Path $AcceptanceRoot '.storage-chronicle-testlab-marker.json') $markerJson
        Write-NewUtf8File (Join-Path $AcceptanceRoot 'StorageChronicleTestVolume.json') $markerJson
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
    if ($CreateVhdx) {
        $image = Get-DiskImage -ImagePath $VhdxPath -ErrorAction Stop
        $imageVolumes = @($image | Get-Disk | Get-Partition | Get-Volume)
        if (-not $image.Attached -or $imageVolumes.Count -ne 1 -or [string]$imageVolumes[0].UniqueId -ne $createdVhdxVolumeUniqueId -or [string]$volume.UniqueId -ne $createdVhdxVolumeUniqueId) {
            throw 'The acceptance path does not resolve to the single volume attached from the exact newly created VHDX.'
        }
        $imageDisk = @($image | Get-Disk)
        if ($imageDisk.Count -ne 1 -or [string]$imageDisk[0].UniqueId -ne $createdVhdxDiskUniqueId) { throw 'The attached image disk identity changed after VHDX creation.' }
    }
    $isNtfs = $null -ne $volume -and $volume.FileSystem -eq 'NTFS'
    $nonNtfsVolume = if ($NonNtfsRoot) { Get-VolumeForPath $NonNtfsRoot } else { $null }
    $nonNtfsReady = $null -ne $nonNtfsVolume -and $nonNtfsVolume.FileSystem -ne 'NTFS'
    $device = $DevicePath
    if ([string]::IsNullOrWhiteSpace($device) -and $AcceptanceRoot) {
        if ($volume -and $volume.DriveLetter) { $device = "\\.\$($volume.DriveLetter):" }
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
            $imageDisk = if ($image -and $image.Attached) { @($image | Get-Disk -ErrorAction SilentlyContinue) } else { @() }
            $imageVolumes = if ($image -and $image.Attached) { @($image | Get-Disk | Get-Partition | Get-Volume) } else { @() }
            $imagePathMatches = $image -and [IO.Path]::GetFullPath([string]$image.ImagePath).Equals([IO.Path]::GetFullPath($VhdxPath), [StringComparison]::OrdinalIgnoreCase)
            $identityMatches = $CreateVhdx -and $imageDisk.Count -eq 1 -and $imageVolumes.Count -eq 1 -and [string]$imageDisk[0].UniqueId -eq $createdVhdxDiskUniqueId -and [string]$imageVolumes[0].UniqueId -eq $createdVhdxVolumeUniqueId
            $vhdxAttached = $null -ne $image -and $image.Attached -and $imagePathMatches -and $isNtfs -and ($identityMatches -or -not $CreateVhdx)
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
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
    $currentVersion = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
    $displayVersion = if ($null -ne $os.PSObject.Properties['DisplayVersion']) { [string]$os.DisplayVersion } elseif ($null -ne $currentVersion) { [string]$currentVersion.DisplayVersion } else { '' }
    $buildNumber = if ($null -ne $os.PSObject.Properties['BuildNumber']) { [string]$os.BuildNumber } elseif ($null -ne $currentVersion) { [string]$currentVersion.CurrentBuild } else { '' }
    $manifest.Environment = [ordered]@{
        ComputerName = $env:COMPUTERNAME
        OS = [Environment]::OSVersion.VersionString
        ProductName = [string]$os.Caption
        DisplayVersion = $displayVersion
        Build = $buildNumber
        Architecture = if ([Environment]::Is64BitOperatingSystem) { 'x64' } else { 'x86' }
        IsAdministrator = $admin
        AcceptanceRoot = $AcceptanceRoot
        EvidenceRoot = $artifactRoot
        NonNtfsRoot = $NonNtfsRoot
        TestId = $runId
        TestLabRoot = $TestLabRoot
        TestLabVolumeUniqueId = if ($testLabVolume) { $testLabVolume.UniqueId } else { $null }
        AcceptanceMarkerRole = if ($acceptanceMarker) { $acceptanceMarker.Role } else { $null }
        NonNtfsMarkerRole = if ($nonNtfsMarker) { $nonNtfsMarker.Role } else { $null }
        DevicePath = $device
        VhdxPath = $VhdxPath
        VhdxRoot = $VhdxRoot
        VhdxDiskUniqueId = $createdVhdxDiskUniqueId
        VhdxVolumeUniqueId = $createdVhdxVolumeUniqueId
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
    if ($createdVhdx -and $VhdxPath -and (Test-Path -LiteralPath $VhdxPath -PathType Leaf)) {
        try {
            $image = Get-DiskImage -ImagePath $VhdxPath -ErrorAction Stop
            if (-not [IO.Path]::GetFullPath([string]$image.ImagePath).Equals([IO.Path]::GetFullPath($VhdxPath), [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Refusing to detach: image lookup did not return the exact VHDX file created by this run.'
            }
            if ($image.Attached) { Dismount-DiskImage -ImagePath $VhdxPath -ErrorAction Stop }
            $afterDetach = Get-DiskImage -ImagePath $VhdxPath -ErrorAction Stop
            if ($afterDetach.Attached) { throw 'The newly created VHDX remains attached after the detach request.' }
            $manifest.VhdxDetachStatus = 'DETACHED_VERIFIED'
            Save-FinalManifest
        } catch {
            $exitCode = 1
            $detachFailure = $_.Exception.Message
            $manifest.VhdxDetachStatus = 'FAILED'
            $manifest.Tests += [ordered]@{ Capability = 'vhdx-detach'; Status = 'FAILED'; Reason = $_.Exception.Message }
            $manifest.Status = 'FAILED'
            $manifest.ExitCode = 1
            $manifest.AcceptanceEligible = $false
            Save-FinalManifest
        }
    } elseif ($createdVhdx) {
        $exitCode = 1
        $detachFailure = 'The created VHDX path is missing; attached state cannot be verified.'
        $manifest.VhdxDetachStatus = 'UNVERIFIABLE_VHDX_PATH_MISSING'
        $manifest.Tests += [ordered]@{ Capability = 'vhdx-detach'; Status = 'FAILED'; Reason = 'The created VHDX path is missing; attached state cannot be verified.' }
        $manifest.Status = 'FAILED'
        $manifest.ExitCode = 1
        $manifest.AcceptanceEligible = $false
        Save-FinalManifest
    } elseif ($createdVhdx -and $exitCode -eq 0) {
        $exitCode = 1
    }
    # Preserve the uniquely created VHDX and all evidence for manual, marker-verified cleanup.
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], 'Process') }
    Stop-Transcript | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($detachFailure)) {
        throw "VHDX detach could not be verified; acceptance is failed and the image requires manual attention: $detachFailure"
    }
}
