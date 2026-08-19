[CmdletBinding()]
param(
    [string]$CandidateRoot,
    [string]$Windows11Iso,
    [string]$Windows10Iso,
    [string]$OutputPath,
    [double]$MinimumMemoryGiB = 8,
    [double]$MinimumFreeGiB = 30
)

$ErrorActionPreference = 'Stop'

function New-Check {
    param(
        [string]$Name,
        [ValidateSet('PASS', 'FAIL', 'NOT_EXECUTED', 'REQUIRES_USER_ACTION')]
        [string]$Status,
        [string]$Detail,
        [string]$Remediation
    )

    [pscustomobject]@{
        Name = $Name
        Status = $Status
        Detail = $Detail
        Remediation = $Remediation
    }
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-UnsafeRoot {
    param([string]$Root)

    if ([string]::IsNullOrWhiteSpace($Root)) { return $false }
    $full = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $blocked = @(
        [Environment]::GetFolderPath('MyDocuments'),
        [Environment]::GetFolderPath('CommonApplicationData'),
        [Environment]::GetFolderPath('ProgramFiles'),
        [Environment]::GetFolderPath('System'),
        (Get-Location).Path
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
        [IO.Path]::GetFullPath($_).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    }

    foreach ($item in $blocked) {
        if ($full.Equals($item, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($item + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

$checks = [System.Collections.Generic.List[object]]::new()
$isAdmin = Test-IsAdministrator
$os = Get-CimInstance Win32_OperatingSystem
$computer = Get-CimInstance Win32_ComputerSystem
$processor = Get-CimInstance Win32_Processor | Select-Object -First 1
$memoryGiB = [math]::Round($computer.TotalPhysicalMemory / 1GB, 2)
$hyperVModule = [bool](Get-Module -ListAvailable -Name Hyper-V)
$vmms = Get-Service vmms -ErrorAction SilentlyContinue
$is64BitHost = [Environment]::Is64BitOperatingSystem

$checks.Add((New-Check 'Windows edition' $(if ($os.Caption -match 'Professional|Enterprise|Education') { 'PASS' } else { 'FAIL' }) $os.Caption 'Windows 10/11 Professional or Enterprise is required for Hyper-V TestLab.'))
$checks.Add((New-Check 'x64 host' $(if ($is64BitHost) { 'PASS' } else { 'FAIL' }) ("Is64BitOperatingSystem={0}; WMI={1}" -f $is64BitHost, $os.OSArchitecture) 'Use an x64 Windows host.'))
$checks.Add((New-Check 'Virtualization firmware' $(if ($processor.VirtualizationFirmwareEnabled) { 'PASS' } else { 'FAIL' }) ([string]$processor.VirtualizationFirmwareEnabled) 'Enable hardware virtualization in BIOS/UEFI manually; Codex must not change firmware settings.'))
$checks.Add((New-Check 'SLAT' $(if ($processor.SecondLevelAddressTranslationExtensions) { 'PASS' } else { 'FAIL' }) ([string]$processor.SecondLevelAddressTranslationExtensions) 'Use a host with SLAT support.'))
$checks.Add((New-Check 'Physical memory' $(if ($memoryGiB -ge $MinimumMemoryGiB) { 'PASS' } else { 'FAIL' }) ("{0} GiB available" -f $memoryGiB) ("At least {0} GiB is required for the configured TestLab." -f $MinimumMemoryGiB)))
$checks.Add((New-Check 'Administrator token' $(if ($isAdmin) { 'PASS' } else { 'REQUIRES_USER_ACTION' }) ("IsAdministrator={0}" -f $isAdmin) 'Run the preflight from an elevated PowerShell for Hyper-V feature inspection and VM control.'))
$checks.Add((New-Check 'Hyper-V PowerShell module' $(if ($hyperVModule) { 'PASS' } else { 'FAIL' }) ("Available={0}" -f $hyperVModule) 'Install/enable Hyper-V on a supported Windows edition, then reopen PowerShell.'))

if (-not $isAdmin) {
    $checks.Add((New-Check 'Hyper-V optional feature state' 'REQUIRES_USER_ACTION' 'The optional feature query requires an administrator token.' 'Rerun this read-only preflight elevated.'))
} else {
    try {
        $feature = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All
        $checks.Add((New-Check 'Hyper-V optional feature state' $(if ($feature.State -eq 'Enabled') { 'PASS' } else { 'FAIL' }) ([string]$feature.State) 'Enable Hyper-V manually and restart the host if required; Codex must not do this automatically.'))
    } catch {
        $checks.Add((New-Check 'Hyper-V optional feature state' 'NOT_EXECUTED' $_.Exception.Message 'Rerun elevated on a supported Windows edition.'))
    }
}

$checks.Add((New-Check 'Hyper-V service' $(if ($vmms -and $vmms.Status -eq 'Running') { 'PASS' } elseif ($vmms) { 'FAIL' } else { 'NOT_EXECUTED' }) $(if ($vmms) { "Status=$($vmms.Status)" } else { 'vmms service not found' }) 'Hyper-V must be enabled and the VMMS service available before TestLab construction.'))

if ($CandidateRoot) {
    $rootFull = [IO.Path]::GetFullPath($CandidateRoot)
    $rootExists = Test-Path -LiteralPath $rootFull -PathType Container
    $unsafe = Test-UnsafeRoot $rootFull
    $checks.Add((New-Check 'Candidate TestLab root' $(if ($unsafe) { 'FAIL' } elseif ($rootExists) { 'PASS' } else { 'REQUIRES_USER_ACTION' }) ("Path={0}; Exists={1}; Unsafe={2}" -f $rootFull, $rootExists, $unsafe) 'Choose a user-approved dedicated directory outside the repository, user documents, ProgramData, Program Files, and monitored media.'))
    if ($rootExists) {
        $drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($rootFull).TrimEnd('\').TrimEnd(':')) -PSProvider FileSystem -ErrorAction SilentlyContinue
        if ($drive) {
            $freeGiB = [math]::Round($drive.Free / 1GB, 2)
            $checks.Add((New-Check 'Candidate volume free space' $(if ($freeGiB -ge $MinimumFreeGiB) { 'PASS' } else { 'FAIL' }) ("{0} GiB free" -f $freeGiB) ("At least {0} GiB free is required for OS/data VHDX and artifacts." -f $MinimumFreeGiB)))
        } else {
            $checks.Add((New-Check 'Candidate volume free space' 'NOT_EXECUTED' 'The candidate root volume could not be resolved.' 'Choose an existing local volume and rerun preflight.'))
        }
    } else {
        $checks.Add((New-Check 'Candidate volume free space' 'NOT_EXECUTED' 'The candidate root does not exist.' 'Obtain user approval, create the dedicated root, and rerun preflight.'))
    }
} else {
    $checks.Add((New-Check 'Candidate TestLab root' 'REQUIRES_USER_ACTION' 'No candidate root was supplied.' 'Obtain user approval for SC_TESTLAB_ROOT and rerun preflight with -CandidateRoot.'))
}

foreach ($iso in @(
        [pscustomobject]@{ Name = 'Windows 11 ISO'; Path = $Windows11Iso },
        [pscustomobject]@{ Name = 'Windows 10 22H2 ISO'; Path = $Windows10Iso }
    )) {
    if ([string]::IsNullOrWhiteSpace($iso.Path)) {
        $checks.Add((New-Check $iso.Name 'REQUIRES_USER_ACTION' 'No ISO path supplied.' 'Obtain user approval for an official Microsoft ISO path; do not download from an unofficial source.'))
    } elseif (Test-Path -LiteralPath $iso.Path -PathType Leaf) {
        $checks.Add((New-Check $iso.Name 'PASS' ([IO.Path]::GetFullPath($iso.Path)) 'Use only an official ISO and retain its user-approved path outside Git.'))
    } else {
        $checks.Add((New-Check $iso.Name 'FAIL' ("ISO not found: {0}" -f $iso.Path) 'Provide an existing official ISO path after user approval.'))
    }
}

$blocking = @($checks | Where-Object Status -in @('FAIL', 'NOT_EXECUTED', 'REQUIRES_USER_ACTION'))
$result = [pscustomobject]@{
    SchemaVersion = 1
    GeneratedUtc = [DateTimeOffset]::UtcNow
    ComputerName = $env:COMPUTERNAME
    ProductName = $os.Caption
    OsVersion = $os.Version
    OsBuild = $os.BuildNumber
    Architecture = $os.OSArchitecture
    HyperVModuleAvailable = $hyperVModule
    IsAdministrator = $isAdmin
    ReadyForTestLab = ($blocking.Count -eq 0)
    Checks = @($checks)
    BlockingChecks = @($blocking.Name)
}

$json = $result | ConvertTo-Json -Depth 8
if ($OutputPath) {
    $parent = Split-Path -Parent $OutputPath
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    Set-Content -LiteralPath $OutputPath -Value $json -Encoding UTF8
}
Write-Output $json
if (-not $result.ReadyForTestLab) { exit 2 }
exit 0
