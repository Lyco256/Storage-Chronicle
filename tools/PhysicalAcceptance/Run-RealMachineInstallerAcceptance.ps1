[CmdletBinding()]
param(
    [string]$BundleRoot = $PSScriptRoot,
    [Parameter(Mandatory = $true)][string]$TestDataRoot,
    [Parameter(Mandatory = $true)][string]$NonAdminCredentialReference,
    [Parameter(Mandatory = $true)][string]$NonAdminUser,
    [Parameter(Mandatory = $true)][string]$SessionUser,
    [switch]$SkipConfirmation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$target = $null
try {
$BundleRoot = [IO.Path]::GetFullPath($BundleRoot)
$manifestPath = Join-Path $BundleRoot 'bundle-manifest.json'
$hashPath = Join-Path $BundleRoot 'hash-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Bundle manifest is missing: $manifestPath" }
if (-not (Test-Path -LiteralPath $hashPath -PathType Leaf)) { throw "Hash manifest is missing: $hashPath" }
$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
$target = [string]$manifest.TargetOs
if ($target -notin @('Windows10-22H2', 'Windows11')) { throw "Unsupported bundle target: $target" }
$hashManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $hashPath | ConvertFrom-Json
$integrityFailures = [System.Collections.Generic.List[string]]::new()
foreach ($entry in @($hashManifest.Files)) {
    $path = Join-Path $BundleRoot ([string]$entry.RelativePath)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { [void]$integrityFailures.Add("Missing payload: $path"); continue }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
    if ($actual -ne [string]$entry.SHA256) { [void]$integrityFailures.Add("Hash mismatch: $path") }
}
if ($integrityFailures.Count -gt 0) { throw (($integrityFailures -join [Environment]::NewLine)) }

$os = Get-CimInstance -ClassName Win32_OperatingSystem
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
$insideBundle = $testDataRootFull.Equals($bundleRootTrimmed, [StringComparison]::OrdinalIgnoreCase) -or $testDataRootFull.StartsWith($bundleRootTrimmed + '\', [StringComparison]::OrdinalIgnoreCase)
if ($testDataRootFull -match '^[A-Za-z]:$' -or $testDataRootFull -in $blockedRoots -or $insideBundle) { throw "Refusing system, volume, or bundle test root: $TestDataRoot" }
if (-not (Test-Path -LiteralPath $testDataRootFull -PathType Container)) { throw "Approved test root does not exist: $TestDataRoot" }
$markerFiles = @(Get-ChildItem -LiteralPath $testDataRootFull -Filter '.storage-chronicle-testlab-marker.json' -File -ErrorAction SilentlyContinue)
if ($markerFiles.Count -ne 1) { throw 'The approved test root must contain exactly one .storage-chronicle-testlab-marker.json marker created by the TestLab/data-volume preparation step.' }
$logicalDisk = Get-CimInstance -ClassName Win32_LogicalDisk -Filter "DeviceID='$($testDataRootFull.Substring(0, 2))'" -ErrorAction SilentlyContinue
$freeSpaceGiB = if ($null -eq $logicalDisk) { 0 } else { [math]::Round([double]$logicalDisk.FreeSpace / 1GB, 2) }
if ($freeSpaceGiB -lt 10) { throw "Insufficient free space on the test volume: ${freeSpaceGiB} GiB; at least 10 GiB is required." }
$installPath = Join-Path $env:ProgramFiles 'Storage Chronicle'
$programDataRoot = Join-Path $env:ProgramData 'Storage Chronicle'
$historyPath = Join-Path $programDataRoot 'history'
$registeredProducts = @(Get-ItemProperty -Path @('HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*') -ErrorAction SilentlyContinue | Where-Object { [string]$_.DisplayName -eq 'Storage Chronicle' })
$existingService = Get-CimInstance -ClassName Win32_Service -Filter "Name='StorageChronicleAgent'" -ErrorAction SilentlyContinue
$programDataEntries = if (Test-Path -LiteralPath $programDataRoot -PathType Container) { @(Get-ChildItem -LiteralPath $programDataRoot -Force -ErrorAction Stop) } else { @() }
$targetIsClean = $registeredProducts.Count -eq 0 -and $null -eq $existingService -and -not (Test-Path -LiteralPath $installPath -PathType Container) -and $programDataEntries.Count -eq 0
if (-not $targetIsClean) { throw "The physical acceptance target is not clean: ProductCount=$($registeredProducts.Count); ServicePresent=$($null -ne $existingService); InstallPathPresent=$(Test-Path -LiteralPath $installPath -PathType Container); ProgramDataEntryCount=$($programDataEntries.Count). Use a disposable target or clean it with explicit user-approved steps before running." }

$resultsRoot = Join-Path $BundleRoot 'results'
New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null
$preflight = [ordered]@{ Schema = 'StorageChronicle.RealMachineInstallerPreflight.v1'; GeneratedUtc = [DateTimeOffset]::UtcNow; TargetOs = $target; ProductName = $os.Caption; DisplayVersion = $osDisplayVersion; Build = $osBuild; IsAdministrator = $isAdmin; IsX64 = $isX64; TestDataRoot = $testDataRootFull; TestDataMarker = $markerFiles[0].FullName; FreeSpaceGiB = $freeSpaceGiB; InstallPath = $installPath; ProgramDataRoot = $programDataRoot; HistoryPath = $historyPath; ExistingProductCount = $registeredProducts.Count; ExistingService = $null -ne $existingService; ExistingProgramDataEntryCount = $programDataEntries.Count; AcceptanceEligible = $false }
$preflightPath = Join-Path $resultsRoot 'real-machine-preflight.json'
$preflight | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $preflightPath -Encoding UTF8
if (-not $SkipConfirmation) {
    $answer = Read-Host "Type YES to run the real-machine installer matrix on $($os.Caption) using $testDataRootFull"
    if ($answer -cne 'YES') { throw 'User did not confirm the real-machine installer run.' }
}

$installerScript = Join-Path $BundleRoot 'Test-Installer.ps1'
$driverScript = Join-Path $BundleRoot 'Invoke-RealInstallerCase.ps1'
$env:STORAGE_CHRONICLE_ACCEPTANCE_TEST_ROOT = $testDataRootFull
$arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $installerScript, '-MsiPath', (Join-Path $BundleRoot 'StorageChronicle.msi'), '-UpdatedMsiPath', (Join-Path $BundleRoot 'StorageChronicle.updated.msi'), '-RollbackMsiPath', (Join-Path $BundleRoot 'StorageChronicle.rollback.msi'), '-TargetOs', $target, '-ExecutionMode', 'Local', '-DriverScript', $driverScript, '-HistoryPath', 'C:\ProgramData\Storage Chronicle\history', '-InstallPath', 'C:\Program Files\Storage Chronicle', '-StoragePermissionPath', 'C:\ProgramData\Storage Chronicle\history', '-NonAdminUser', $NonAdminUser, '-NonAdminCredentialReference', $NonAdminCredentialReference, '-SessionUser', $SessionUser, '-OutputDirectory', (Join-Path $resultsRoot 'installer'), '-Execute', '-AllowLocalIsolatedExecution')
$powerShell = Get-Command powershell.exe -ErrorAction Stop
& $powerShell.Source @arguments
$runExitCode = $LASTEXITCODE
& (Join-Path $BundleRoot 'Collect-PhysicalAcceptanceResults.ps1') -BundleRoot $BundleRoot | Out-Host
if ($runExitCode -ne 0) { throw "The physical installer matrix returned exit code $runExitCode; inspect the collected result manifest." }
exit 0
} catch {
    $fallbackResults = Join-Path ([IO.Path]::GetFullPath($BundleRoot)) 'results'
    New-Item -ItemType Directory -Force -Path $fallbackResults | Out-Null
    $failure = [ordered]@{ Schema = 'StorageChronicle.RealMachineInstallerFailure.v1'; GeneratedUtc = [DateTimeOffset]::UtcNow; TargetOs = if ($target) { $target } else { $null }; Status = 'WRONG_ENVIRONMENT'; Reason = $_.Exception.Message; AcceptanceEligible = $false }
    $failure | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $fallbackResults 'real-machine-preflight-failure.json') -Encoding UTF8
    [Console]::Error.WriteLine("WRONG_ENVIRONMENT: $($failure.Reason)")
    exit 2
}
