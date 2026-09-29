[CmdletBinding()]
param(
    [string]$BundleRoot = $PSScriptRoot,
    [Parameter(Mandatory = $true)][string]$TestDataRoot,
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedHashManifestSha256,
    [Parameter(Mandatory = $true)][string]$NonAdminCredentialReference,
    [Parameter(Mandatory = $true)][string]$NonAdminUser,
    [Parameter(Mandatory = $true)][string]$SessionUser
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$target = $null
$evidenceRunRoot = $null

function Assert-NoReparseAncestors([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($fullPath.StartsWith('\\', [StringComparison]::Ordinal)) { throw "UNC paths are not accepted: $fullPath" }
    $rootPath = [IO.Path]::GetPathRoot($fullPath)
    if ($rootPath -notmatch '^[A-Za-z]:\\$') { throw "A local drive path is required: $fullPath" }
    $cursor = $rootPath
    foreach ($segment in $fullPath.Substring($rootPath.Length).Split([char[]]@('\\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $segment
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse points are not accepted in an acceptance path: $cursor" }
        }
    }
}

function Write-NewJson([string]$Path, $Value) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 12))
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}

try {
$BundleRoot = [IO.Path]::GetFullPath($BundleRoot)
Assert-NoReparseAncestors $BundleRoot
$manifestPath = Join-Path $BundleRoot 'bundle-manifest.json'
$hashPath = Join-Path $BundleRoot 'hash-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Bundle manifest is missing: $manifestPath" }
    if (-not (Test-Path -LiteralPath $hashPath -PathType Leaf)) { throw "Hash manifest is missing: $hashPath" }
    $actualHashManifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $hashPath).Hash
    if (-not $actualHashManifestSha256.Equals($ExpectedHashManifestSha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'The hash-manifest SHA-256 does not match the separately supplied, user-approved fingerprint.' }
    $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
    $target = [string]$manifest.TargetOs
    if ($target -notin @('Windows10-22H2', 'Windows11')) { throw "Unsupported bundle target: $target" }
    if (@($manifest.MissingInputs).Count -gt 0) { throw ('Bundle is incomplete; missing inputs: ' + (@($manifest.MissingInputs) -join ', ')) }
    $hashManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $hashPath | ConvertFrom-Json
    $integrityFailures = [System.Collections.Generic.List[string]]::new()
    $requiredPayload = @('StorageChronicle.msi', 'StorageChronicle.updated.msi', 'StorageChronicle.rollback.msi', 'Test-Installer.ps1', 'Invoke-RealInstallerCase.ps1', 'Probe-WriteAccess.ps1', 'Collect-PhysicalAcceptanceResults.ps1')
    $hashEntries = @($hashManifest.Files | ForEach-Object { [string]$_.RelativePath })
    foreach ($relative in $requiredPayload) {
        $path = Join-Path $BundleRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { [void]$integrityFailures.Add("Missing required payload: $path") }
        if ($hashEntries -notcontains $relative) { [void]$integrityFailures.Add("Required payload is absent from hash manifest: $relative") }
    }
    foreach ($relative in @('agent/StorageChronicle.Agent.exe', 'session-agent/StorageChronicle.SessionAgent.exe', 'ui/StorageChronicle.UI.Desktop.exe')) {
        $path = Join-Path $BundleRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { [void]$integrityFailures.Add("Missing self-contained executable: $path") }
        if ($hashEntries -notcontains $relative) { [void]$integrityFailures.Add("Self-contained executable is absent from hash manifest: $relative") }
    }
    $seenHashPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($hashManifest.Files)) {
    $relativePath = [string]$entry.RelativePath
    if ([string]::IsNullOrWhiteSpace($relativePath) -or [IO.Path]::IsPathRooted($relativePath) -or $relativePath -match '(^|[\\/])\.\.([\\/]|$)' -or $relativePath.Contains(':') -or $entry.SHA256 -notmatch '^[A-Fa-f0-9]{64}$' -or -not $seenHashPaths.Add($relativePath)) { throw "Hash manifest contains an unsafe, duplicate, or malformed entry: $relativePath" }
    $path = [IO.Path]::GetFullPath((Join-Path $BundleRoot $relativePath))
    if (-not $path.StartsWith($BundleRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Hash manifest path escapes the bundle: $relativePath" }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { [void]$integrityFailures.Add("Missing payload: $path"); continue }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
    if ($actual -ne [string]$entry.SHA256) { [void]$integrityFailures.Add("Hash mismatch: $path") }
}
if ($integrityFailures.Count -gt 0) { throw (($integrityFailures -join [Environment]::NewLine)) }

$os = Get-CimInstance -ClassName Win32_OperatingSystem
$computerSystem = Get-CimInstance -ClassName Win32_ComputerSystem
if ([string]$computerSystem.Model -match '(?i)virtual|vmware|virtualbox|kvm|hyper-v|qemu') { throw "Physical acceptance refuses a virtualized host model: $($computerSystem.Manufacturer) $($computerSystem.Model)" }
$currentVersion = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
$osDisplayVersion = if ($null -ne $os.PSObject.Properties['DisplayVersion']) { [string]$os.DisplayVersion } elseif ($null -ne $currentVersion) { [string]$currentVersion.DisplayVersion } else { '' }
$osBuild = if ($null -ne $os.PSObject.Properties['BuildNumber']) { [string]$os.BuildNumber } else { [string]$currentVersion.CurrentBuild }
$isAdmin = ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$isX64 = [Environment]::Is64BitOperatingSystem
$targetMatches = if ($target -eq 'Windows10-22H2') { [string]$os.Caption -match 'Windows 10' -and ($osDisplayVersion -eq '22H2' -or $osBuild -eq '19045') } else { [string]$os.Caption -match 'Windows 11' }
if (-not $isAdmin -or -not $isX64 -or -not $targetMatches) { throw "Wrong environment: Product=$($os.Caption), DisplayVersion=$osDisplayVersion, Build=$osBuild, x64=$isX64, admin=$isAdmin, target=$target" }

$testDataRootFull = [IO.Path]::GetFullPath($TestDataRoot).TrimEnd('\')
$blockedRoots = @('C:', 'C:\', $env:WINDIR, $env:ProgramFiles, $env:ProgramData) | ForEach-Object { try { [IO.Path]::GetFullPath($_).TrimEnd('\') } catch { $_ } }
$bundleRootTrimmed = $BundleRoot.TrimEnd('\')
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).TrimEnd('\')
$insideBundle = $testDataRootFull.Equals($bundleRootTrimmed, [StringComparison]::OrdinalIgnoreCase) -or $testDataRootFull.StartsWith($bundleRootTrimmed + '\', [StringComparison]::OrdinalIgnoreCase)
if ($testDataRootFull -match '^[A-Za-z]:$' -or $testDataRootFull -in $blockedRoots -or $insideBundle -or $testDataRootFull.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or $testDataRootFull.StartsWith($repositoryRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or $testDataRootFull -match '(?i)\\OneDrive\\|\\Documents\\') { throw "Refusing system, volume, repository, synced-folder, or bundle test root: $TestDataRoot" }
if (-not (Test-Path -LiteralPath $testDataRootFull -PathType Container)) { throw "Approved test root does not exist: $TestDataRoot" }
    Assert-NoReparseAncestors $testDataRootFull
    $markerFiles = @(Get-ChildItem -LiteralPath $testDataRootFull -Filter '.storage-chronicle-testlab-marker.json' -File -ErrorAction SilentlyContinue)
    if ($markerFiles.Count -ne 1) { throw 'The approved test root must contain exactly one .storage-chronicle-testlab-marker.json marker created by the TestLab/data-volume preparation step.' }
    $volumeMarkerPath = Join-Path $testDataRootFull 'StorageChronicleTestVolume.json'
    if (-not (Test-Path -LiteralPath $volumeMarkerPath -PathType Leaf)) { throw "The approved test root lacks the required volume marker: $volumeMarkerPath" }
    $marker = Get-Content -Raw -Encoding UTF8 -LiteralPath $markerFiles[0].FullName | ConvertFrom-Json
    $volumeMarker = Get-Content -Raw -Encoding UTF8 -LiteralPath $volumeMarkerPath | ConvertFrom-Json
    $parsedTestId = [guid]::Empty
    if ([string]$marker.Schema -ne 'StorageChronicle.TestLabDataMarker.v1' -or [string]$marker.Role -ne 'Workload' -or [string]$marker.VolumeLabel -ne 'SC_TEST_VOLUME' -or [string]$marker.FileSystem -ne 'NTFS' -or -not [guid]::TryParse([string]$marker.TestId, [ref]$parsedTestId)) { throw 'The execution marker schema, workload role, volume label/filesystem, or TestId GUID is invalid.' }
    if ([string]$volumeMarker.Schema -ne 'StorageChronicle.TestLabDataMarker.v1' -or [string]$volumeMarker.Role -ne 'Workload' -or [string]$volumeMarker.VolumeLabel -ne 'SC_TEST_VOLUME' -or [string]$volumeMarker.FileSystem -ne 'NTFS' -or [string]$volumeMarker.TestId -ne [string]$marker.TestId) { throw 'The volume marker schema, role, label, filesystem, or TestId does not match the execution marker.' }
    if ([string]$marker.VolumeUniqueId -ne [string]$volumeMarker.VolumeUniqueId -or [string]::IsNullOrWhiteSpace([string]$marker.VolumeUniqueId)) { throw 'The marker pair does not bind to one stable volume identity.' }
$logicalDisk = Get-CimInstance -ClassName Win32_LogicalDisk -Filter "DeviceID='$($testDataRootFull.Substring(0, 2))'" -ErrorAction SilentlyContinue
$freeSpaceGiB = if ($null -eq $logicalDisk) { 0 } else { [math]::Round([double]$logicalDisk.FreeSpace / 1GB, 2) }
if ($freeSpaceGiB -lt 40) { throw "Insufficient free space on the test volume: ${freeSpaceGiB} GiB; at least 40 GiB is required." }
$testVolume = Get-Volume -FilePath $testDataRootFull -ErrorAction Stop
if ([string]$testVolume.FileSystem -ne 'NTFS' -or [string]$testVolume.DriveType -ne 'Fixed' -or [string]$testVolume.UniqueId -ne [string]$marker.VolumeUniqueId) { throw 'The test root is not on the exact marked local fixed NTFS volume.' }
$installPath = Join-Path $env:ProgramFiles 'Storage Chronicle'
$programDataRoot = Join-Path $env:ProgramData 'Storage Chronicle'
$historyPath = Join-Path $programDataRoot 'history'
$registeredProducts = @(Get-ItemProperty -Path @('HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*') -ErrorAction SilentlyContinue | Where-Object { [string]$_.DisplayName -eq 'Storage Chronicle' })
$existingService = Get-CimInstance -ClassName Win32_Service -Filter "Name='StorageChronicleAgent'" -ErrorAction SilentlyContinue
$programDataEntries = if (Test-Path -LiteralPath $programDataRoot -PathType Container) { @(Get-ChildItem -LiteralPath $programDataRoot -Force -ErrorAction Stop) } else { @() }
$targetIsClean = $registeredProducts.Count -eq 0 -and $null -eq $existingService -and -not (Test-Path -LiteralPath $installPath -PathType Container) -and -not (Test-Path -LiteralPath $programDataRoot) -and $programDataEntries.Count -eq 0
if (-not $targetIsClean) { throw "The physical acceptance target is not clean: ProductCount=$($registeredProducts.Count); ServicePresent=$($null -ne $existingService); InstallPathPresent=$(Test-Path -LiteralPath $installPath -PathType Container); ProgramDataEntryCount=$($programDataEntries.Count). Use a disposable target or clean it with explicit user-approved steps before running." }

$BundleRoot = [IO.Path]::GetFullPath($BundleRoot)
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
Assert-NoReparseAncestors (Split-Path -Parent $EvidenceRoot)
if (Test-Path -LiteralPath $EvidenceRoot) { throw "EvidenceRoot must be a new path; refusing to write into existing data: $EvidenceRoot" }
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).TrimEnd('\')
$forbiddenEvidenceRoots = @($BundleRoot, $repositoryRoot, [IO.Path]::GetFullPath($env:ProgramData), [IO.Path]::GetFullPath($env:ProgramFiles))
foreach ($forbiddenRoot in $forbiddenEvidenceRoots) {
    $normalizedForbiddenRoot = $forbiddenRoot.TrimEnd('\')
    if ($EvidenceRoot.Equals($normalizedForbiddenRoot, [StringComparison]::OrdinalIgnoreCase) -or $EvidenceRoot.StartsWith($normalizedForbiddenRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "EvidenceRoot overlaps a protected bundle, repository, or application directory: $EvidenceRoot" }
}
if ($EvidenceRoot -match '(?i)\\OneDrive\\|\\Documents\\' -or $EvidenceRoot.StartsWith('\\', [StringComparison]::Ordinal)) { throw 'EvidenceRoot must be local and outside synchronized or user-document folders.' }
$evidenceVolume = Get-Volume -FilePath (Split-Path -Parent $EvidenceRoot) -ErrorAction Stop
if ([string]$evidenceVolume.FileSystem -ne 'NTFS' -or [string]$evidenceVolume.DriveType -ne 'Fixed') { throw 'EvidenceRoot parent must be on a local fixed NTFS volume.' }
$runId = $parsedTestId.ToString('D')
$evidenceRunRoot = Join-Path $EvidenceRoot "installer-acceptance-$runId"
if (Test-Path -LiteralPath $evidenceRunRoot) { throw "The unique evidence run path already exists; refusing overwrite: $evidenceRunRoot" }
$testRootTrimmed = $testDataRootFull.TrimEnd('\')
if ($evidenceRunRoot.Equals($testRootTrimmed, [StringComparison]::OrdinalIgnoreCase) -or $evidenceRunRoot.StartsWith($testRootTrimmed + '\', [StringComparison]::OrdinalIgnoreCase) -or $testRootTrimmed.StartsWith($evidenceRunRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'EvidenceRoot and TestDataRoot must not overlap.' }
$msiHashes = [ordered]@{}
foreach ($name in @('StorageChronicle.msi', 'StorageChronicle.updated.msi', 'StorageChronicle.rollback.msi')) {
    $msiHashes[$name] = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $BundleRoot $name)).Hash
}
$preflight = [ordered]@{ Schema = 'StorageChronicle.RealMachineInstallerPreflight.v1'; RunId = $runId; GeneratedUtc = [DateTimeOffset]::UtcNow; ComputerName = $env:COMPUTERNAME; TargetOs = $target; ProductName = $os.Caption; DisplayVersion = $osDisplayVersion; Build = $osBuild; IsAdministrator = $isAdmin; IsX64 = $isX64; TestDataRoot = $testDataRootFull; TestDataVolumeUniqueId = [string]$testVolume.UniqueId; TestDataMarker = $markerFiles[0].FullName; FreeSpaceGiB = $freeSpaceGiB; InstallPath = $installPath; ProgramDataRoot = $programDataRoot; ProgramDataRootPresentBeforeRun = Test-Path -LiteralPath $programDataRoot; HistoryPath = $historyPath; EvidenceRoot = $evidenceRunRoot; MsiSha256 = $msiHashes; ExistingProductCount = $registeredProducts.Count; ExistingService = $null -ne $existingService; ExistingProgramDataEntryCount = $programDataEntries.Count; AcceptanceEligible = $false }
New-Item -ItemType Directory -Path $evidenceRunRoot | Out-Null
$preflightPath = Join-Path $evidenceRunRoot 'real-machine-preflight.json'
Write-NewJson $preflightPath $preflight

$installerScript = Join-Path $BundleRoot 'Test-Installer.ps1'
$driverScript = Join-Path $BundleRoot 'Invoke-RealInstallerCase.ps1'
$env:STORAGE_CHRONICLE_ACCEPTANCE_TEST_ROOT = $testDataRootFull
$arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $installerScript, '-MsiPath', (Join-Path $BundleRoot 'StorageChronicle.msi'), '-UpdatedMsiPath', (Join-Path $BundleRoot 'StorageChronicle.updated.msi'), '-RollbackMsiPath', (Join-Path $BundleRoot 'StorageChronicle.rollback.msi'), '-TargetOs', $target, '-TargetKind', 'PhysicalMachine', '-ExecutionMode', 'Local', '-DriverScript', $driverScript, '-HistoryPath', $historyPath, '-InstallPath', $installPath, '-StoragePermissionPath', $historyPath, '-NonAdminUser', $NonAdminUser, '-NonAdminCredentialReference', $NonAdminCredentialReference, '-SessionUser', $SessionUser, '-OutputDirectory', $evidenceRunRoot, '-RunId', $runId, '-ExpectedComputerName', $env:COMPUTERNAME, '-ExpectedHashManifestSha256', $ExpectedHashManifestSha256, '-ConfirmDedicatedPhysicalMachine', '-Execute', '-AllowLocalIsolatedExecution')
$powerShell = Get-Command powershell.exe -ErrorAction Stop
& $powerShell.Source @arguments
$runExitCode = $LASTEXITCODE
if ($runExitCode -ne 0) { throw "The physical installer matrix returned exit code $runExitCode; inspect the collected result manifest." }
exit 0
} catch {
    $failure = [ordered]@{ Schema = 'StorageChronicle.RealMachineInstallerFailure.v1'; GeneratedUtc = [DateTimeOffset]::UtcNow; TargetOs = if ($target) { $target } else { $null }; Status = 'WRONG_ENVIRONMENT'; Reason = $_.Exception.Message; AcceptanceEligible = $false }
    if ($evidenceRunRoot -and (Test-Path -LiteralPath $evidenceRunRoot -PathType Container)) {
        try { Write-NewJson (Join-Path $evidenceRunRoot ('failure-' + [guid]::NewGuid().ToString('N') + '.json')) $failure } catch { [Console]::Error.WriteLine("Could not preserve installer failure evidence without overwriting: $($_.Exception.Message)") }
    }
    [Console]::Error.WriteLine("WRONG_ENVIRONMENT: $($failure.Reason)")
    exit 2
}
