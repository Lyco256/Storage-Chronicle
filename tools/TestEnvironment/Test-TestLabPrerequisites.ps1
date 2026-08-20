[CmdletBinding()]
param(
    [string]$CandidateRoot,
    [string]$Windows11Iso,
    [string]$Windows10Iso,
    [string]$OutputPath,
    [double]$MinimumMemoryGiB = 6,
    [double]$MinimumFreeGiB = 40
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestLab.Common.ps1')

function New-Check {
    param([string]$Name, [ValidateSet('PASS', 'FAIL', 'NOT_EXECUTED', 'REQUIRES_USER_ACTION')][string]$Status, [string]$Detail, [string]$Remediation)
    [pscustomobject]@{ Name = $Name; Status = $Status; Detail = $Detail; Remediation = $Remediation }
}

function Write-PreflightResult {
    param([Parameter(Mandatory = $true)]$Value, [int]$ExitCode)
    $json = $Value | ConvertTo-Json -Depth 16
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $parent = Split-Path -Parent $OutputPath
        if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
        Set-Content -LiteralPath $OutputPath -Value $json -Encoding UTF8
    }
    Write-Output $json
    exit $ExitCode
}

function Get-OptionalPropertyValue {
    param([AllowNull()]$InputObject, [Parameter(Mandatory = $true)][string]$Name)
    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-HostSecurityAudit {
    $hyperVFeatureDetected = $null
    $hyperVFeatureQueryStatus = 'NOT_EXECUTED'
    try {
        $hyperVFeatureName = 'Microsoft-Hyper-V-' + 'All'
        $feature = Get-WindowsOptionalFeature -Online -FeatureName $hyperVFeatureName -ErrorAction Stop
        $hyperVFeatureDetected = $null -ne $feature -and [string]$feature.State -in @('Enabled', 'Enable Pending')
        $hyperVFeatureQueryStatus = 'READ'
    } catch {
        $hyperVFeatureQueryStatus = 'REQUIRES_USER_ACTION'
    }
    $deviceGuard = $null
    try { $deviceGuard = Get-CimInstance -Namespace 'root\Microsoft\Windows\DeviceGuard' -ClassName Win32_DeviceGuard -ErrorAction Stop } catch { }
    $registry = $null
    try { $registry = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard' -ErrorAction Stop } catch { }
    $memoryIntegrity = $null
    if ($null -ne $deviceGuard) { $memoryIntegrity = [bool]((Get-OptionalPropertyValue $deviceGuard 'SecurityServicesRunning') -contains 2) }
    elseif ($null -ne $registry -and $null -ne (Get-OptionalPropertyValue $registry 'EnableVirtualizationBasedSecurity')) { $memoryIntegrity = [bool](Get-OptionalPropertyValue $registry 'EnableVirtualizationBasedSecurity') -and [bool](Get-OptionalPropertyValue $registry 'HypervisorEnforcedCodeIntegrity') }
    [ordered]@{
        HyperVFeatureDetected = $hyperVFeatureDetected
        HyperVFeatureQueryStatus = $hyperVFeatureQueryStatus
        VbsEnabled = if ($null -eq $deviceGuard) { $null } else { [bool](Get-OptionalPropertyValue $deviceGuard 'VirtualizationBasedSecurityStatus') -in @(1, 2) }
        MemoryIntegrityEnabled = $memoryIntegrity
        DeviceGuardQueryStatus = if ($null -eq $deviceGuard) { 'NOT_AVAILABLE' } else { 'READ' }
        RegistryDeviceGuardValues = if ($null -eq $registry) { $null } else { [ordered]@{ EnableVirtualizationBasedSecurity = Get-OptionalPropertyValue $registry 'EnableVirtualizationBasedSecurity'; HypervisorEnforcedCodeIntegrity = Get-OptionalPropertyValue $registry 'HypervisorEnforcedCodeIntegrity' } }
    }
}

function Get-HostTpmAudit {
    try {
        $tpm = Get-CimInstance -Namespace 'root\cimv2\security\microsofttpm' -ClassName Win32_Tpm -ErrorAction Stop | Select-Object -First 1
        return [ordered]@{ Present = $true; Ready = [bool]$tpm.IsEnabled().IsReady; Enabled = [bool]$tpm.IsEnabled().IsEnabled; Activated = [bool]$tpm.IsActivated().IsActivated; QueryStatus = 'READ' }
    } catch {
        return [ordered]@{ Present = $null; Ready = $null; Enabled = $null; Activated = $null; QueryStatus = 'NOT_AVAILABLE' }
    }
}

function Get-VirtualBoxVmAudit {
    param([string[]]$Names)
    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($name in $Names) {
        $exists = $false
        $state = $null
        $baseline = $false
        try {
            $info = Assert-ExactTestLabVm -Name $name
            $exists = $true
            $state = [string]$info.State
            $snapshot = Invoke-VBoxManage @('snapshot', $name, 'showvminfo', 'SC-CLEAN-BASELINE') -AllowNonZero
            $baseline = $snapshot.ExitCode -eq 0
        } catch { }
        [void]$items.Add([ordered]@{ Name = $name; Exists = $exists; State = $state; BaselineSnapshot = $baseline })
    }
    return @($items)
}

$checks = [System.Collections.Generic.List[object]]::new()
$result = [ordered]@{
    Schema = 'StorageChronicle.VirtualBoxHostPreflight.v1'
    GeneratedUtc = [DateTimeOffset]::UtcNow
    Status = 'BLOCKED'
    AcceptanceEligible = $false
    ReadyForProvisioning = $false
    ReadyForFunctionalAcceptance = $false
    Host = [ordered]@{}
    Checks = @()
    BlockingChecks = @()
    HumanHandoff = $null
}

try {
    $os = Get-CimInstance Win32_OperatingSystem
    $computer = Get-CimInstance Win32_ComputerSystem
    $processor = Get-CimInstance Win32_Processor | Select-Object -First 1
    $security = Get-HostSecurityAudit
    $tpm = Get-HostTpmAudit
    $result.Host = [ordered]@{
        ProductName = $os.Caption
        OsVersion = $os.Version
        OsBuild = $os.BuildNumber
        Architecture = $os.OSArchitecture
        Cpu = $processor.Name
        Cores = $computer.NumberOfLogicalProcessors
        TotalMemoryGiB = [math]::Round($computer.TotalPhysicalMemory / 1GB, 2)
        AvailableMemoryGiB = [math]::Round($os.FreePhysicalMemory / 1MB, 2)
        WmiVirtualizationFirmwareEnabled = [bool]$processor.VirtualizationFirmwareEnabled
        HyperVModulePresent = [bool](Get-Module -ListAvailable -Name Hyper-V)
        HyperVFeatureDetected = $security.HyperVFeatureDetected
        HyperVFeatureQueryStatus = $security.HyperVFeatureQueryStatus
        VbsEnabled = $security.VbsEnabled
        MemoryIntegrityEnabled = $security.MemoryIntegrityEnabled
        DeviceGuardQueryStatus = $security.DeviceGuardQueryStatus
        Tpm = $tpm
    }
    $checks.Add((New-Check 'Windows host' 'PASS' ([string]$os.Caption) 'Windows is required for the VirtualBox Windows guest acceptance.'))
    $checks.Add((New-Check 'x64 host' $(if ([Environment]::Is64BitOperatingSystem) { 'PASS' } else { 'FAIL' }) ([string]$os.OSArchitecture) 'Use an x64 Windows host.'))
    $checks.Add((New-Check 'RAM profile' $(if ($result.Host.AvailableMemoryGiB -ge $MinimumMemoryGiB) { 'PASS' } else { 'FAIL' }) ("Available={0} GiB; minimum={1} GiB" -f $result.Host.AvailableMemoryGiB, $MinimumMemoryGiB) 'Close heavy host applications; do not reduce the 4 GiB guest profile.'))

    if ([string]::IsNullOrWhiteSpace($CandidateRoot)) { throw 'A user-approved CandidateRoot/TestLabRoot is required.' }
    $root = [IO.Path]::GetFullPath($CandidateRoot)
    $rootExists = Test-Path -LiteralPath $root -PathType Container
    $unsafe = Test-IsProtectedTestLabPath $root
    $checks.Add((New-Check 'VirtualBox TestLabRoot' $(if ($unsafe) { 'FAIL' } elseif ($rootExists) { 'PASS' } else { 'REQUIRES_USER_ACTION' }) ("Path={0}; Exists={1}; Unsafe={2}" -f $root, $rootExists, $unsafe) 'Use an existing dedicated directory outside the repository, OneDrive, Documents, ProgramData, Program Files, Windows, and monitored media.'))
    if (-not $rootExists -or $unsafe) { throw "The supplied TestLabRoot is missing or unsafe: $root" }
    $volume = Get-Volume -DriveLetter ([IO.Path]::GetPathRoot($root).Substring(0, 1)) -ErrorAction Stop
    $freeDiskGiB = [math]::Round($volume.SizeRemaining / 1GB, 2)
    $result.Host.TestLabRoot = $root
    $result.Host.FileSystem = $volume.FileSystem
    $result.Host.FreeDiskGiB = $freeDiskGiB
    $checks.Add((New-Check 'TestLabRoot filesystem' $(if ([string]$volume.FileSystem -eq 'NTFS') { 'PASS' } else { 'FAIL' }) ([string]$volume.FileSystem) 'Use a local NTFS TestLab volume.'))
    $checks.Add((New-Check 'TestLabRoot free space' $(if ($freeDiskGiB -ge $MinimumFreeGiB) { 'PASS' } else { 'FAIL' }) ("{0} GiB free; minimum={1} GiB" -f $freeDiskGiB, $MinimumFreeGiB) 'Free space or move TestLabRoot to an approved local volume.'))

    $vboxPath = $null
    $version = $null
    $hostInfo = $null
    $result.Host.HostVirtualizationCapability = $false
    try {
        $vboxPath = Get-VBoxManagePath
        $version = Get-VBoxVersion
        $hostInfo = Invoke-VBoxManage @('list', 'hostinfo')
        $result.Host.HostVirtualizationCapability = [bool]($hostInfo.Output -match '(?im)VT-x/AMD-V:\s+enabled')
        $result.Host.VirtualBoxPath = $vboxPath
        $result.Host.VirtualBoxVersion = $version
        $result.Host.VirtualBoxBaseline = '7.2.16'
        $result.Host.VirtualBoxVersionPolicy = '7.2.16 baseline; same-series 7.2.x fallback requires reporting'
        $result.Host.HostInfo = $hostInfo.Output
        $extensionPacks = Invoke-VBoxManage @('list', 'extpacks')
        $result.Host.ExtensionPacks = $extensionPacks.Output
        $result.Host.VirtualBoxVms = Get-VirtualBoxVmAudit -Names @('SC-Test-W11-VBox', 'SC-Test-W10-VBox')
        $checks.Add((New-Check 'VirtualBox version' $(if ($version -match '^7\.2\.') { 'PASS' } else { 'FAIL' }) $version 'Use Oracle VirtualBox 7.2.16, or report a later stable 7.2.x release before using it. Do not use an unapproved major/minor release.'))
        $checks.Add((New-Check 'VirtualBox hardware virtualization' $(if ($result.Host.HostVirtualizationCapability) { 'PASS' } else { 'REQUIRES_USER_ACTION' }) ("VBoxManage hostinfo reports VT-x/AMD-V enabled={0}; WMI={1}" -f $result.Host.HostVirtualizationCapability, $result.Host.WmiVirtualizationFirmwareEnabled) 'If a real VM cannot start, use the fixed firmware handoff; do not disable security features automatically.'))
        $checks.Add((New-Check 'Extension Pack' $(if ($extensionPacks.Output -match '(?im)^Extension Packs:\s+0\s*$') { 'PASS' } else { 'FAIL' }) $extensionPacks.Output 'Do not install Oracle VirtualBox Extension Pack for this TestLab.'))
    } catch {
        $result.Host.VirtualBoxPath = $vboxPath
        $result.Host.VirtualBoxVersion = $version
        $result.Host.HostInfo = $null
        $result.Host.VirtualBoxVms = @()
        $result.Host.ExtensionPacks = $null
        $checks.Add((New-Check 'VirtualBox version' 'REQUIRES_USER_ACTION' 'VBoxManage.exe is not installed or is not discoverable.' 'Install the approved Oracle VirtualBox 7.2.16 Windows host package through normal UAC, then rerun this preflight.'))
        $checks.Add((New-Check 'VirtualBox hardware virtualization' 'REQUIRES_USER_ACTION' 'VBoxManage hostinfo was not available.' 'Rerun after VirtualBox installation; only if a real VM cannot start, inspect firmware VT-x/AMD-V without disabling security features.'))
        $checks.Add((New-Check 'Extension Pack' 'REQUIRES_USER_ACTION' 'VBoxManage list extpacks was not available.' 'Rerun after VirtualBox installation and verify that no Extension Pack is installed.'))
    }
    $result.Host.HyperVDetectedForAudit = [bool]$result.Host.HyperVModulePresent -or [bool]$result.Host.HyperVFeatureDetected
    foreach ($iso in @([pscustomobject]@{ Name = 'Windows 11 ISO'; Path = $Windows11Iso }, [pscustomobject]@{ Name = 'Windows 10 22H2 ISO'; Path = $Windows10Iso })) {
        if ([string]::IsNullOrWhiteSpace($iso.Path)) { $checks.Add((New-Check $iso.Name 'REQUIRES_USER_ACTION' 'No ISO path supplied.' 'Provide a user-approved official Microsoft ISO path when that guest is selected.')) }
        elseif (Test-Path -LiteralPath $iso.Path -PathType Leaf -and [IO.Path]::GetExtension($iso.Path) -ieq '.iso') { $checks.Add((New-Check $iso.Name 'PASS' ([IO.Path]::GetFullPath($iso.Path)) 'Use an official, unmodified ISO only.')) }
        else { $checks.Add((New-Check $iso.Name 'FAIL' ("ISO not found or not .iso: {0}" -f $iso.Path) 'Provide an existing official ISO path outside Git.')) }
    }
    $result.Host.Windows11Iso = $Windows11Iso
    $result.Host.Windows10Iso = $Windows10Iso
    $result.Host.ResourceProfile = if ($freeDiskGiB -ge 100) { 'full-provisioning' } elseif ($freeDiskGiB -ge 60) { 'existing-baseline-only' } else { 'functional-smoke-only' }
    $blocking = @($checks | Where-Object Status -in @('FAIL', 'NOT_EXECUTED', 'REQUIRES_USER_ACTION'))
    $result.Checks = @($checks)
    $result.BlockingChecks = @($blocking.Name)
    $result.ReadyForProvisioning = $blocking.Count -eq 0
    $result.ReadyForFunctionalAcceptance = $result.ReadyForProvisioning -and [bool]$result.Host.HostVirtualizationCapability
    $result.Status = if ($result.ReadyForFunctionalAcceptance) { 'PASSED' } elseif ($result.ReadyForProvisioning) { 'REQUIRES_USER_ACTION' } else { 'BLOCKED' }
    if (-not $result.ReadyForFunctionalAcceptance) {
        $result.HumanHandoff = [ordered]@{
            Blocked = 'VirtualBox TestLab preflight'
            Reason = ($blocking | ForEach-Object { "$($_.Name): $($_.Detail)" }) -join '; '
            WhyUserActionIsRequired = 'VirtualBox installation, firmware settings, official ISO selection, and approved disposable storage are host/user-controlled operations; Codex remains a normal host process and does not self-elevate or bypass UAC.'
            DoThis = @('Install the Oracle VirtualBox 7.2.16 Windows host package through its normal UAC prompt if it is absent.', 'Enable Intel VT-x/AMD-V in firmware only if a real VirtualBox VM cannot start.', 'Provide the official Windows ISO paths and an existing dedicated NTFS TestLabRoot outside the repository/OneDrive.', 'Do not install Extension Pack, enable shared folders, shared clipboard, drag-and-drop, raw disks, or disable security features solely for this test.')
            ExpectedResult = 'VBoxManage --version is 7.2.x, VBoxManage list hostinfo reports hardware virtualization usable, TestLabRoot is safe NTFS with at least 40 GiB free, and every selected ISO exists.'
            DoNotDo = @('Do not run Codex as Administrator.', 'Do not use unofficial or modified ISO media.', 'Do not map a physical disk or the host C: drive into a guest.')
            ResumeCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`" -CandidateRoot `"$CandidateRoot`" -Windows11Iso `"$Windows11Iso`" -Windows10Iso `"$Windows10Iso`" -OutputPath `"$OutputPath`""
            SendBack = @('Status, ReadyForProvisioning, ReadyForFunctionalAcceptance, VirtualBoxVersion, HostVirtualizationCapability, TestLabRoot, FileSystem, FreeDiskGiB, and BlockingChecks only; never send passwords or product keys.')
        }
        Write-PreflightResult $result 2
    }
    Write-PreflightResult $result 0
}
catch {
    $result.Status = 'BLOCKED'
    $result.AcceptanceEligible = $false
    $result.ReadyForProvisioning = $false
    $result.ReadyForFunctionalAcceptance = $false
    $result.BlockingChecks = @($_.Exception.Message)
    $result.HumanHandoff = [ordered]@{
        Blocked = 'VirtualBox TestLab preflight'
        Reason = $_.Exception.Message
        WhyUserActionIsRequired = 'The requested host capability or user-approved input is unavailable; no VM mutation was attempted.'
        DoThis = @('Complete the user-controlled installation, firmware, ISO, and disposable-root preparation described by Requirements 34.', 'Re-run the ResumeCommand after the preparation.')
        ExpectedResult = 'The preflight JSON has ReadyForFunctionalAcceptance=true and Status=PASSED.'
        DoNotDo = @('Do not bypass UAC or disable security controls as a workaround.')
        ResumeCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`" -CandidateRoot `"$CandidateRoot`" -Windows11Iso `"$Windows11Iso`" -Windows10Iso `"$Windows10Iso`" -OutputPath `"$OutputPath`""
        SendBack = @('Only the non-secret preflight fields and BlockingChecks; no credentials, product keys, or passwords.')
    }
    Write-PreflightResult $result 2
}
