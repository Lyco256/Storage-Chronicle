[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BundleRoot,
    [Parameter(Mandatory = $true)][string]$TestDataRoot,
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-PathWithin([string]$Path, [string]$Root) {
    $pathFull = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    return $pathFull.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase) -or $pathFull.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparsePath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($fullPath.StartsWith('\\', [StringComparison]::Ordinal)) { throw "UNC paths are not accepted: $fullPath" }
    $rootPath = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($rootPath) -or $rootPath -notmatch '^[A-Za-z]:\\$') { throw "A local drive path is required: $fullPath" }
    $cursor = $rootPath
    foreach ($segment in $fullPath.Substring($rootPath.Length).Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $segment
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse points are not accepted in an acceptance path: $cursor" }
        }
    }
}

function Write-NewJson([string]$Path, $Value) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 16))
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}

$BundleRoot = [IO.Path]::GetFullPath($BundleRoot)
$TestDataRoot = [IO.Path]::GetFullPath($TestDataRoot).TrimEnd('\')
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot).TrimEnd('\')
if (-not (Test-Path -LiteralPath $BundleRoot -PathType Container)) { throw "BundleRoot does not exist: $BundleRoot" }
if (-not (Test-Path -LiteralPath $TestDataRoot -PathType Container)) { throw "The approved test-data root does not exist: $TestDataRoot" }
if (-not (Test-Path -LiteralPath $EvidenceRoot -PathType Container)) { throw "EvidenceRoot must already exist: $EvidenceRoot" }
Assert-NoReparsePath $BundleRoot
Assert-NoReparsePath $TestDataRoot
Assert-NoReparsePath $EvidenceRoot

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..')).TrimEnd('\')
$forbiddenRoots = @(
    [IO.Path]::GetPathRoot($env:SystemDrive),
    $env:WINDIR,
    $env:ProgramFiles,
    $env:ProgramData,
    $repositoryRoot,
    $BundleRoot
) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { [IO.Path]::GetFullPath([string]$_).TrimEnd('\') }
$syncPattern = '(?i)(^|\\)(OneDrive|Documents)(\\|$)'
foreach ($path in @($TestDataRoot, $EvidenceRoot)) {
    if ($path -match $syncPattern -or $path -match '^[A-Za-z]:$') { throw "Test/evidence paths cannot be a volume root or synchronized/document folder: $path" }
    foreach ($forbidden in $forbiddenRoots) {
        if (Test-PathWithin -Path $path -Root $forbidden) { throw "Test/evidence path overlaps a protected root: $path" }
    }
}
if ((Test-PathWithin -Path $TestDataRoot -Root $EvidenceRoot) -or (Test-PathWithin -Path $EvidenceRoot -Root $TestDataRoot)) { throw 'TestDataRoot and EvidenceRoot must not overlap.' }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $EvidenceRoot ('windows10-preflight-' + [guid]::NewGuid().ToString('N') + '.json') }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-PathWithin -Path $OutputPath -Root $EvidenceRoot) -or (Test-Path -LiteralPath $OutputPath)) { throw 'OutputPath must be a new file directly beneath the approved EvidenceRoot; existing outputs are never overwritten.' }
if (-not [IO.Path]::GetDirectoryName($OutputPath).Equals($EvidenceRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputPath must be a direct child of EvidenceRoot.' }

$checks = [System.Collections.Generic.List[object]]::new()
function Add-Check([string]$Name, [bool]$Pass, [string]$Detail) {
    $checks.Add([ordered]@{ Name = $Name; Status = if ($Pass) { 'PASS' } else { 'FAIL' }; Detail = $Detail })
}

$os = $null
$computerSystem = $null
$displayVersion = ''
$buildNumber = ''
$isAdmin = $false
$isX64 = [Environment]::Is64BitOperatingSystem
try {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
    $currentVersion = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
    $displayVersion = if ($null -ne $os.PSObject.Properties['DisplayVersion']) { [string]$os.DisplayVersion } else { [string]$currentVersion.DisplayVersion }
    $buildNumber = if ($null -ne $os.PSObject.Properties['BuildNumber']) { [string]$os.BuildNumber } else { [string]$currentVersion.CurrentBuild }
    $isAdmin = ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Add-Check 'Windows10Product' ([string]$os.Caption -match 'Windows 10') ([string]$os.Caption)
    Add-Check 'Windows10_22H2' ($displayVersion -eq '22H2' -or $buildNumber -eq '19045') ("DisplayVersion={0}; Build={1}" -f $displayVersion, $buildNumber)
    Add-Check 'ArchitectureX64' $isX64 ([Environment]::OSVersion.VersionString + '; x64=' + $isX64)
    Add-Check 'AdministratorToken' $isAdmin ('IsAdministrator=' + $isAdmin)
} catch {
    Add-Check 'WindowsHostInventory' $false $_.Exception.Message
}

$machineName = $env:COMPUTERNAME
$manufacturer = ''
$model = ''
$nonVirtualModel = $false
try {
    $computerSystem = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
    $manufacturer = [string]$computerSystem.Manufacturer
    $model = [string]$computerSystem.Model
    $nonVirtualModel = $model -notmatch '(?i)virtual|vmware|virtualbox|kvm|hyper-v|qemu|parallels'
    Add-Check 'NoVirtualMachineModelIndicator' $nonVirtualModel ("Manufacturer={0}; Model={1}; this is a heuristic, not hardware attestation" -f $manufacturer, $model)
} catch {
    Add-Check 'ComputerSystemInventory' $false $_.Exception.Message
}

$testVolume = $null
$partition = $null
$disk = $null
$freeGiB = 0.0
$volumeIdentity = ''
$driveLetter = ''
try {
    if ($TestDataRoot -notmatch '^(?<Drive>[A-Za-z]):\\') { throw 'TestDataRoot must be on a local drive-letter volume.' }
    $driveLetter = $Matches.Drive.ToUpperInvariant()
    $testVolume = Get-Volume -FilePath $TestDataRoot -ErrorAction Stop
    if ($null -eq $testVolume -or [string]::IsNullOrWhiteSpace([string]$testVolume.UniqueId)) { throw 'The test-data volume identity could not be resolved.' }
    $volumeIdentity = [string]$testVolume.UniqueId
    $logicalDisk = Get-CimInstance -ClassName Win32_LogicalDisk -Filter "DeviceID='$driveLetter`:'" -ErrorAction Stop
    $freeGiB = [math]::Round([double]$logicalDisk.FreeSpace / 1GB, 2)
    $partitions = @(Get-Partition -DriveLetter $driveLetter -ErrorAction Stop)
    if ($partitions.Count -ne 1) { throw "Expected exactly one partition for the test drive; found $($partitions.Count)." }
    $partition = $partitions[0]
    $disk = Get-Disk -Number $partition.DiskNumber -ErrorAction Stop
    $rolesSafe = -not [bool]$partition.IsSystem -and -not [bool]$partition.IsBoot -and -not [bool]$disk.IsSystem -and -not [bool]$disk.IsBoot -and [string]$disk.BusType -ne 'USB' -and [string]$disk.OperationalStatus -ne 'Offline'
    $targetVolumeSafe = [string]$testVolume.FileSystem -eq 'NTFS' -and [string]$testVolume.DriveType -eq 'Fixed' -and $rolesSafe -and $driveLetter -ne $env:SystemDrive.TrimEnd(':').ToUpperInvariant()
    Add-Check 'DedicatedFixedNtfsVolume' $targetVolumeSafe ("Drive={0}; VolumeId={1}; FileSystem={2}; DriveType={3}; DiskBus={4}; IsSystem={5}; IsBoot={6}" -f $driveLetter, $volumeIdentity, $testVolume.FileSystem, $testVolume.DriveType, $disk.BusType, $disk.IsSystem, $disk.IsBoot)
    Add-Check 'TestVolumeFreeSpace' ($freeGiB -ge 40) ("FreeSpaceGiB={0}; minimum=40" -f $freeGiB)
} catch {
    Add-Check 'TestVolumeIdentityAndRole' $false $_.Exception.Message
}

$marker = $null
$volumeMarker = $null
try {
    $entries = @(Get-ChildItem -LiteralPath $TestDataRoot -Force -ErrorAction Stop)
    $reparseChildren = @($entries | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    $expectedNames = @('.storage-chronicle-testlab-marker.json', 'StorageChronicleTestVolume.json')
    $unexpected = @($entries | Where-Object { $_.Name -notin $expectedNames })
    $markerPath = Join-Path $TestDataRoot $expectedNames[0]
    $volumeMarkerPath = Join-Path $TestDataRoot $expectedNames[1]
    if (@($entries | Where-Object Name -eq $expectedNames[0]).Count -ne 1 -or @($entries | Where-Object Name -eq $expectedNames[1]).Count -ne 1) { throw 'Both expected run-ownership marker files must exist exactly once.' }
    if ($reparseChildren.Count -ne 0) { throw 'The test-data root contains a reparse point; marker files were not opened.' }
    $marker = Get-Content -Raw -Encoding UTF8 -LiteralPath $markerPath | ConvertFrom-Json
    $volumeMarker = Get-Content -Raw -Encoding UTF8 -LiteralPath $volumeMarkerPath | ConvertFrom-Json
    $parsedRunId = [guid]::Empty
    $markersValid = [string]$marker.Schema -eq 'StorageChronicle.TestLabDataMarker.v1' -and
        [string]$marker.Role -eq 'Workload' -and [string]$marker.VolumeLabel -eq 'SC_TEST_VOLUME' -and
        [string]$marker.FileSystem -eq 'NTFS' -and [guid]::TryParse([string]$marker.TestId, [ref]$parsedRunId) -and
        [string]$marker.VolumeUniqueId -eq $volumeIdentity -and
        [string]$volumeMarker.Schema -eq 'StorageChronicle.TestLabDataMarker.v1' -and
        [string]$volumeMarker.Role -eq 'Workload' -and [string]$volumeMarker.VolumeLabel -eq 'SC_TEST_VOLUME' -and
        [string]$volumeMarker.FileSystem -eq 'NTFS' -and [string]$volumeMarker.TestId -eq [string]$marker.TestId -and
        [string]$volumeMarker.VolumeUniqueId -eq $volumeIdentity
    Add-Check 'RunOwnershipMarkers' ($markersValid -and $reparseChildren.Count -eq 0 -and $unexpected.Count -eq 0) ("TestId={0}; VolumeIdMatch={1}; ReparseChildren={2}; UnexpectedEntries={3}" -f $marker.TestId, ([string]$marker.VolumeUniqueId -eq $volumeIdentity), $reparseChildren.Count, ($unexpected.Name -join ','))
} catch {
    Add-Check 'RunOwnershipMarkers' $false $_.Exception.Message
}

$uninspectedProfiles = [System.Collections.Generic.List[string]]::new()
try {
    $profiles = @(Get-CimInstance -ClassName Win32_UserProfile -ErrorAction Stop)
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $profileUninstallPaths = [System.Collections.Generic.List[string]]::new()
    $profileInstallPaths = [System.Collections.Generic.List[string]]::new()
    $currentProfileFound = $false
    foreach ($profile in $profiles) {
        if ([bool]$profile.Special) { continue }
        $profileSid = [string]$profile.SID
        if ([string]::IsNullOrWhiteSpace($profileSid)) { throw 'A non-special Windows user profile has no SID.' }
        if ($profileSid -eq $currentSid) { $currentProfileFound = $true }
        $profilePath = [string]$profile.LocalPath
        if ([string]::IsNullOrWhiteSpace($profilePath)) { throw "Windows user profile $profileSid has no local profile path." }
        $profilePath = [Environment]::ExpandEnvironmentVariables($profilePath)
        $profileInstallPaths.Add((Join-Path $profilePath 'AppData\Local\Storage Chronicle'))
        if (-not [bool]$profile.Loaded) {
            $uninspectedProfiles.Add($profileSid)
            continue
        }
        foreach ($uninstallSubkey in @(
            'Software\Microsoft\Windows\CurrentVersion\Uninstall',
            'Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
        )) {
            $profileUninstallPaths.Add("Registry::HKEY_USERS\$profileSid\$uninstallSubkey")
        }
    }
    if (-not $currentProfileFound) { throw 'The current user profile is missing from the Windows profile inventory.' }

    $uninstallPaths = @(
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    ) + @($profileUninstallPaths)
    $installedProducts = [System.Collections.Generic.List[object]]::new()
    foreach ($uninstallPath in $uninstallPaths) {
        if (Test-Path -LiteralPath $uninstallPath -PathType Container -ErrorAction Stop) {
            foreach ($uninstallKey in @(Get-ChildItem -LiteralPath $uninstallPath -ErrorAction Stop)) {
                $product = Get-ItemProperty -LiteralPath $uninstallKey.PSPath -ErrorAction Stop
                if ([string]$product.DisplayName -eq 'Storage Chronicle') { $installedProducts.Add($product) }
            }
        }
    }
    $historyRoot = Join-Path $env:ProgramData 'Storage Chronicle'
    $historyEntries = if (Test-Path -LiteralPath $historyRoot -PathType Container) { @(Get-ChildItem -LiteralPath $historyRoot -Force -ErrorAction Stop) } else { @() }
    $installPaths = @((Join-Path $env:ProgramFiles 'Storage Chronicle'))
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) { $installPaths += Join-Path ${env:ProgramFiles(x86)} 'Storage Chronicle' }
    $installPaths += @($profileInstallPaths)
    $existingInstallPaths = @($installPaths | Where-Object { Test-Path -LiteralPath $_ })
    foreach ($protectedPath in @($historyRoot) + $existingInstallPaths) { Assert-NoReparsePath $protectedPath }
    $clean = $installedProducts.Count -eq 0 -and $existingInstallPaths.Count -eq 0 -and -not (Test-Path -LiteralPath $historyRoot) -and $historyEntries.Count -eq 0 -and $uninspectedProfiles.Count -eq 0
    Add-Check 'UnloadedUserProfileHivesAbsent' ($uninspectedProfiles.Count -eq 0) ("ProfilesNotSafelyInspectable={0}" -f ($uninspectedProfiles -join ';'))
    Add-Check 'ExistingProductAndHistoryAbsent' $clean ("RegisteredProducts={0}; ExistingInstallOrUserDataPaths={1}; HistoryRootPresent={2}; HistoryEntries={3}; ProfilesNotSafelyInspectable={4}" -f $installedProducts.Count, ($existingInstallPaths -join ';'), (Test-Path -LiteralPath $historyRoot), $historyEntries.Count, ($uninspectedProfiles -join ';'))
} catch {
    Add-Check 'ExistingProductAndHistoryInventory' $false $_.Exception.Message
}

try {
    $agentServices = @(Get-CimInstance -ClassName Win32_Service -Filter "Name='StorageChronicleAgent'" -ErrorAction Stop)
    Add-Check 'AgentServiceNameAvailable' ($agentServices.Count -eq 0) ("ExistingServiceCount={0}" -f $agentServices.Count)
} catch {
    Add-Check 'AgentServiceInventory' $false $_.Exception.Message
}

try {
    $shares = @(Get-SmbShare -ErrorAction Stop | Where-Object { [string]$_.Name -match '(?i)^(SCAcc|SC[_-]?ACCEPTANCE)' -or (Test-PathWithin -Path ([string]$_.Path) -Root $TestDataRoot) })
    Add-Check 'AcceptanceShareNameAndPathAvailable' ($shares.Count -eq 0) ("ConflictingShares={0}" -f (($shares | ForEach-Object { '{0}:{1}' -f $_.Name, $_.Path }) -join ';'))
} catch {
    Add-Check 'SmbShareInventory' $false $_.Exception.Message
}

$failed = @($checks | Where-Object Status -ne 'PASS')
$payload = [ordered]@{
    Schema = 'StorageChronicle.Windows10PhysicalPreflight.v1'
    GeneratedUtc = [DateTimeOffset]::UtcNow
    Environment = [ordered]@{
        ComputerName = $machineName
        ProductName = if ($os) { [string]$os.Caption } else { '' }
        Manufacturer = $manufacturer
        Model = $model
        DisplayVersion = $displayVersion
        Build = $buildNumber
        Architecture = if ($isX64) { 'x64' } else { 'x86' }
        IsAdministrator = $isAdmin
        PhysicalHostModelHeuristicPassed = $nonVirtualModel
        TestDataRoot = $TestDataRoot
        TestDataVolumeUniqueId = $volumeIdentity
        TestDataVolumeLabel = if ($testVolume) { [string]$testVolume.FileSystemLabel } else { '' }
        TestId = if ($marker) { [string]$marker.TestId } else { '' }
        FreeSpaceGiB = $freeGiB
        EvidenceRoot = $EvidenceRoot
        UninspectedUserProfileCount = if ($uninspectedProfiles) { $uninspectedProfiles.Count } else { 0 }
    }
    Checks = @($checks)
    Ready = $failed.Count -eq 0
    AcceptanceEligible = $false
    Limitations = @('Physical host detection is a model-string heuristic, not hardware attestation.', 'This preflight is read-only; it does not prove runtime write safety or replace the commit-bound source-to-sink audit.')
}
Write-NewJson -Path $OutputPath -Value $payload
$payload | ConvertTo-Json -Depth 16
if (-not $payload.Ready) { exit 2 }
exit 0
