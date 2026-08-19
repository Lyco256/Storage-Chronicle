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
    $markerPresent = Test-Path -LiteralPath $markerPath -PathType Leaf
    Add-Check 'TestDataRoot' ($safe -and $markerPresent) ("Root={0}; systemBlocked={1}; bundleBlocked={2}; marker={3}" -f $TestDataRoot, ($trimmed -in $blocked), $insideBundle, $markerPresent)
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
