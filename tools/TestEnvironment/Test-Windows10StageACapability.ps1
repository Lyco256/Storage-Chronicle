[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$RunId,
    [string]$VmName = 'SC-Test-W10-VBox'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

function Get-Windows10Environment {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
    $currentVersion = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
    $displayVersion = if ($null -ne $os.PSObject.Properties['DisplayVersion']) { [string]$os.DisplayVersion } elseif ($null -ne $currentVersion) { [string]$currentVersion.DisplayVersion } else { '' }
    $build = if ($null -ne $os.PSObject.Properties['BuildNumber']) { [string]$os.BuildNumber } elseif ($null -ne $currentVersion) { [string]$currentVersion.CurrentBuild } else { '' }
    $isWindows10 = [string]$os.Caption -match 'Windows 10'
    $is22H2 = $displayVersion -eq '22H2' -or $build -eq '19045'
    $isX64 = [Environment]::Is64BitOperatingSystem
    return [ordered]@{
        ProductName = [string]$os.Caption
        DisplayVersion = $displayVersion
        Build = $build
        Architecture = if ($isX64) { 'x64' } else { 'x86' }
        IsWindows10 = $isWindows10
        Is22H2 = $is22H2
        IsX64 = $isX64
        IsTarget = $isWindows10 -and $is22H2 -and $isX64
    }
}

function Write-CheckArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$Passed,
        [Parameter(Mandatory = $true)]$Environment,
        [Parameter(Mandatory = $true)]$Details
    )

    $evidencePath = Join-Path $OutputDirectory "$Name-evidence.json"
    $checkPath = Join-Path $OutputDirectory "$Name-check.json"
    $evidence = [ordered]@{
        Schema = 'StorageChronicle.Windows10StageACapabilityEvidence.v1'
        CheckName = $Name
        RunId = $RunId
        TargetOs = 'Windows10-22H2'
        TargetKind = 'VirtualBoxVm'
        VmName = $VmName
        ExecutionMode = 'VM'
        Environment = $Environment
        Probe = $Details
        Status = if ($Passed) { 'PASSED' } else { 'FAILED' }
        AcceptanceEligible = $Passed
        Diagnostic = $false
        EvidenceOrigin = 'real'
        GeneratedUtc = [DateTimeOffset]::UtcNow
    }
    $evidence | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $evidencePath -Encoding UTF8
    $check = [ordered]@{
        Schema = 'StorageChronicle.Windows10StageACheck.v1'
        CheckName = $Name
        TargetOs = 'Windows10-22H2'
        TargetKind = 'VirtualBoxVm'
        VmName = $VmName
        ExecutionMode = 'VM'
        Status = if ($Passed) { 'PASSED' } else { 'FAILED' }
        AcceptanceEligible = $Passed
        Diagnostic = $false
        EvidenceOrigin = 'real'
        EvidencePath = $evidencePath
        RunId = $RunId
        GeneratedUtc = [DateTimeOffset]::UtcNow
    }
    if ($Name -eq 'NoDriver') {
        $check.DriverPresent = if ($Details.Keys -contains 'DriverPresent') { [bool]$Details['DriverPresent'] } else { $null }
    }
    $check | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $checkPath -Encoding UTF8
    return [pscustomobject]@{ CheckPath = $checkPath; EvidencePath = $evidencePath; Passed = $Passed }
}

function Get-CloudFilesProbe {
    param([Parameter(Mandatory = $true)]$Environment)

    $requiredExports = @(
        'CfConnectSyncRoot',
        'CfDisconnectSyncRoot',
        'CfExecute',
        'CfGetPlaceholderInfo',
        'CfGetPlaceholderInfoForHydration',
        'CfGetPlaceholderProperties'
    )
    $module = [IntPtr]::Zero
    $moduleError = 0
    $exports = [ordered]@{}
    try {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class StorageChronicleCloudFilesProbeNative
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeLibrary(IntPtr hModule);
}
'@
        $module = [StorageChronicleCloudFilesProbeNative]::LoadLibrary('cldapi.dll')
        if ($module -eq [IntPtr]::Zero) { $moduleError = [Runtime.InteropServices.Marshal]::GetLastWin32Error() }
        foreach ($export in $requiredExports) {
            $available = $module -ne [IntPtr]::Zero -and [StorageChronicleCloudFilesProbeNative]::GetProcAddress($module, $export) -ne [IntPtr]::Zero
            $exports[$export] = $available
        }
    } catch {
        return [pscustomobject]@{
            Passed = $false
            Details = [ordered]@{ ProbeMethod = 'LoadLibrary/GetProcAddress'; Module = 'cldapi.dll'; ModuleLoaded = $false; ModuleError = $moduleError; RequiredExports = $requiredExports; Exports = $exports; Error = $_.Exception.Message; ApiCalled = $false }
        }
    } finally {
        if ($module -ne [IntPtr]::Zero) { [void][StorageChronicleCloudFilesProbeNative]::FreeLibrary($module) }
    }
    $allExports = @($exports.GetEnumerator() | Where-Object { -not [bool]$_.Value }).Count -eq 0
    return [pscustomobject]@{
        Passed = $Environment.IsTarget -and $module -ne [IntPtr]::Zero -and $allExports
        Details = [ordered]@{ ProbeMethod = 'LoadLibrary/GetProcAddress'; Module = 'cldapi.dll'; ModuleLoaded = $module -ne [IntPtr]::Zero; ModuleError = $moduleError; RequiredExports = $requiredExports; Exports = $exports; Error = $null; ApiCalled = $false }
    }
}

function Get-NoDriverProbe {
    param([Parameter(Mandatory = $true)]$Environment)

    $systemDrivers = @(Get-CimInstance -ClassName Win32_SystemDriver -ErrorAction Stop)
    $matchingSystemDrivers = @($systemDrivers | Where-Object {
        ([string]$_.Name -match '(?i)storage.?chronicle') -or
        ([string]$_.DisplayName -match '(?i)storage.?chronicle') -or
        ([string]$_.PathName -match '(?i)storage.?chronicle')
    } | ForEach-Object {
        [ordered]@{ Name = [string]$_.Name; DisplayName = [string]$_.DisplayName; State = [string]$_.State; PathName = [string]$_.PathName }
    })
    $driverDirectory = Join-Path $env:WINDIR 'System32\drivers'
    $matchingFiles = @(Get-ChildItem -LiteralPath $driverDirectory -File -ErrorAction Stop | Where-Object { $_.Name -match '(?i)storage.?chronicle' } | ForEach-Object { [string]$_.Name })
    $pnputil = Get-Command -Name pnputil.exe -ErrorAction Stop
    $pnputilOutput = @(& $pnputil.Source /enum-drivers 2>&1)
    $pnputilExitCode = $LASTEXITCODE
    if ($pnputilExitCode -ne 0) { throw "pnputil /enum-drivers failed with exit code $pnputilExitCode." }
    $matchingPackages = @($pnputilOutput | Where-Object { [string]$_ -match '(?i)storage.?chronicle' } | ForEach-Object { [string]$_ })
    $driverPresent = $matchingSystemDrivers.Count -gt 0 -or $matchingFiles.Count -gt 0 -or $matchingPackages.Count -gt 0
    return [pscustomobject]@{
        Passed = $Environment.IsTarget -and -not $driverPresent
        Details = [ordered]@{
            ProbeMethod = 'Win32_SystemDriver,System32\\drivers filenames,pnputil /enum-drivers'
            DriverPresent = $driverPresent
            MatchingSystemDrivers = $matchingSystemDrivers
            MatchingDriverFiles = $matchingFiles
            MatchingDriverPackages = $matchingPackages
            PnputilExitCode = $pnputilExitCode
            ProductServiceIsNotDriver = $true
        }
    }
}

$environment = Get-Windows10Environment
$cloudResult = $null
$noDriverResult = $null
try { $cloudResult = Get-CloudFilesProbe -Environment $environment } catch { $cloudResult = [pscustomobject]@{ Passed = $false; Details = [ordered]@{ Error = $_.Exception.Message; ProbeMethod = 'LoadLibrary/GetProcAddress'; ApiCalled = $false } } }
try { $noDriverResult = Get-NoDriverProbe -Environment $environment } catch { $noDriverResult = [pscustomobject]@{ Passed = $false; Details = [ordered]@{ Error = $_.Exception.Message; ProbeMethod = 'Win32_SystemDriver,System32\\drivers filenames,pnputil /enum-drivers' } } }

$cloudArtifact = Write-CheckArtifact -Name 'CloudFilesCapability' -Passed $cloudResult.Passed -Environment $environment -Details $cloudResult.Details
$noDriverArtifact = Write-CheckArtifact -Name 'NoDriver' -Passed $noDriverResult.Passed -Environment $environment -Details ([ordered]@{
    DriverPresent = if ($noDriverResult.Details.Keys -contains 'DriverPresent') { [bool]$noDriverResult.Details['DriverPresent'] } else { $null }
    Probe = $noDriverResult.Details
})
$summary = [ordered]@{
    Schema = 'StorageChronicle.Windows10StageACapabilitySummary.v1'
    RunId = $RunId
    TargetOs = 'Windows10-22H2'
    TargetKind = 'VirtualBoxVm'
    VmName = $VmName
    ExecutionMode = 'VM'
    Environment = $environment
    CloudFiles = $cloudArtifact
    NoDriver = $noDriverArtifact
    AcceptanceEligible = [bool]$cloudArtifact.Passed -and [bool]$noDriverArtifact.Passed
    Status = if ([bool]$cloudArtifact.Passed -and [bool]$noDriverArtifact.Passed) { 'PASSED' } else { 'FAILED' }
    Diagnostic = $false
    EvidenceOrigin = 'real'
    GeneratedUtc = [DateTimeOffset]::UtcNow
}
$summaryPath = Join-Path $OutputDirectory 'capability-summary.json'
$summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-Output ($summary | ConvertTo-Json -Depth 20)
if (-not $summary.AcceptanceEligible) { exit 2 }
exit 0
