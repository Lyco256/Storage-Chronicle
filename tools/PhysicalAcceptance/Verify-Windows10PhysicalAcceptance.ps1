[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BundleRoot,
    [Parameter(Mandatory = $true)][string]$TestDataRoot,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$BundleRoot = [IO.Path]::GetFullPath($BundleRoot)
$TestDataRoot = [IO.Path]::GetFullPath($TestDataRoot)
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $BundleRoot 'results/windows10-preflight.json' }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null

function Add-Check {
    param([string]$Name, [bool]$Pass, [string]$Detail)
    $checks.Add([ordered]@{ Name = $Name; Status = if ($Pass) { 'PASS' } else { 'FAIL' }; Detail = $Detail })
}

$checks = [System.Collections.Generic.List[object]]::new()
$os = Get-CimInstance -ClassName Win32_OperatingSystem
$currentVersion = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
$displayVersion = if ($null -ne $os.PSObject.Properties['DisplayVersion']) { [string]$os.DisplayVersion } elseif ($null -ne $currentVersion) { [string]$currentVersion.DisplayVersion } else { '' }
$buildNumber = if ($null -ne $os.PSObject.Properties['BuildNumber']) { [string]$os.BuildNumber } else { [string]$currentVersion.CurrentBuild }
$isWin10 = [string]$os.Caption -match 'Windows 10'
$is22H2 = $displayVersion -eq '22H2' -or $buildNumber -eq '19045'
$isAdmin = ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$isX64 = [Environment]::Is64BitOperatingSystem
Add-Check 'ProductName' $isWin10 ([string]$os.Caption)
Add-Check 'DisplayVersion' $is22H2 ("DisplayVersion={0}; Build={1}" -f $displayVersion, $buildNumber)
Add-Check 'Architecture' $isX64 ([Environment]::OSVersion.VersionString + '; x64=' + $isX64)
Add-Check 'Administrator' $isAdmin ('IsAdministrator=' + $isAdmin)
if (-not (Test-Path -LiteralPath $TestDataRoot -PathType Container)) { Add-Check 'TestDataRoot' $false 'The approved test-data root does not exist.' } else {
    $trimmed = $TestDataRoot.TrimEnd('\')
    $blocked = @('C:', 'C:\', "$env:WINDIR", "$env:ProgramFiles", "$env:ProgramData") | ForEach-Object { try { [IO.Path]::GetFullPath($_).TrimEnd('\') } catch { $_ } }
    $bundleTrimmed = $BundleRoot.TrimEnd('\\')
    $insideBundle = $trimmed.Equals($bundleTrimmed, [StringComparison]::OrdinalIgnoreCase) -or $trimmed.StartsWith($bundleTrimmed + '\\', [StringComparison]::OrdinalIgnoreCase)
    $safe = $trimmed -notin $blocked -and $trimmed -notmatch '^[A-Za-z]:$' -and -not $insideBundle
    $markerPath = Join-Path $TestDataRoot '.storage-chronicle-testlab-marker.json'
    $volumeMarkerPath = Join-Path $TestDataRoot 'StorageChronicleTestVolume.json'
    $markerPresent = Test-Path -LiteralPath $markerPath -PathType Leaf
    $volumeMarkerPresent = Test-Path -LiteralPath $volumeMarkerPath -PathType Leaf
    $markerValid = $false
    if ($markerPresent -and $volumeMarkerPresent) {
        $marker = Get-Content -Raw -Encoding UTF8 -LiteralPath $markerPath | ConvertFrom-Json
        $volumeMarker = Get-Content -Raw -Encoding UTF8 -LiteralPath $volumeMarkerPath | ConvertFrom-Json
        $markerValid = [string]$marker.Schema -eq 'StorageChronicle.TestLabDataMarker.v1' -and -not [string]::IsNullOrWhiteSpace([string]$marker.TestId) -and [string]$volumeMarker.Schema -eq 'StorageChronicle.TestLabDataMarker.v1' -and [string]$volumeMarker.TestId -eq [string]$marker.TestId
    }
    Add-Check 'TestDataRoot' ($safe -and $markerValid) ("Root={0}; systemBlocked={1}; bundleBlocked={2}; executionMarker={3}; volumeMarker={4}; markerValid={5}" -f $TestDataRoot, ($trimmed -in $blocked), $insideBundle, $markerPresent, $volumeMarkerPresent, $markerValid)
    $driveId = if ($TestDataRoot -match '^[A-Za-z]:') { $TestDataRoot.Substring(0, 2) } else { $null }
    $disk = if ($driveId) { Get-CimInstance -ClassName Win32_LogicalDisk -Filter "DeviceID='$driveId'" -ErrorAction SilentlyContinue } else { $null }
    $freeGiB = if ($null -eq $disk) { 0 } else { [math]::Round([double]$disk.FreeSpace / 1GB, 2) }
    Add-Check 'TestLab VHDX destination and free space' ($null -ne $disk -and $freeGiB -ge 10) ("Volume={0}; FreeSpaceGiB={1}; minimum=10" -f $driveId, $freeGiB)
}
$payload = [ordered]@{
    Schema = 'StorageChronicle.Windows10PhysicalPreflight.v1'
    GeneratedUtc = [DateTimeOffset]::UtcNow
    Environment = [ordered]@{ ProductName = $os.Caption; DisplayVersion = $displayVersion; Build = $buildNumber; Architecture = if ($isX64) { 'x64' } else { 'x86' }; TestDataRoot = $TestDataRoot }
    Checks = @($checks)
    Ready = @($checks | Where-Object Status -eq 'FAIL').Count -eq 0
    AcceptanceEligible = $false
}
$payload | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$payload | ConvertTo-Json -Depth 12
if (-not $payload.Ready) { exit 2 }
exit 0
