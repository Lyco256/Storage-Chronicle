[CmdletBinding()]
param()

Set-StrictMode -Version Latest

function Get-VBoxManagePath {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:VBOXMANAGE_PATH)) { $candidates += $env:VBOXMANAGE_PATH }
    $command = Get-Command VBoxManage.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) { $candidates += $command.Source }
    $candidates += @(
        (Join-Path ${env:ProgramFiles} 'Oracle\VirtualBox\VBoxManage.exe'),
        (Join-Path ${env:ProgramFiles} 'VirtualBox\VBoxManage.exe')
    )
    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace([string]$candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    throw 'VBoxManage.exe is not installed or is not discoverable. Install the Oracle VirtualBox Windows host package through the user-approved UAC handoff; no VM operation was attempted.'
}

function ConvertTo-VBoxArguments([string[]]$Arguments) {
    foreach ($argument in $Arguments) {
        if ($argument -notmatch '[\s"]') { $argument; continue }
        '"' + (($argument -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
    }
}

function Invoke-VBoxManage {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowNonZero
    )

    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = Get-VBoxManagePath
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $argumentListProperty = [Diagnostics.ProcessStartInfo].GetProperty('ArgumentList')
    if ($null -ne $argumentListProperty -and $null -ne $info.ArgumentList) {
        foreach ($argument in $Arguments) { [void]$info.ArgumentList.Add([string]$argument) }
    } else {
        $info.Arguments = (ConvertTo-VBoxArguments $Arguments) -join ' '
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) { throw "Could not start VBoxManage.exe: $($Arguments -join ' ')" }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $result = [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = $stdoutTask.GetAwaiter().GetResult()
        Error = $stderrTask.GetAwaiter().GetResult()
        Arguments = @($Arguments)
    }
    $process.Dispose()
    if (-not $AllowNonZero -and $result.ExitCode -ne 0) {
        throw "VBoxManage failed with exit code $($result.ExitCode): $($result.Error.Trim())"
    }
    return $result
}

function Get-VBoxVersion {
    $result = Invoke-VBoxManage @('--version')
    return ($result.Output.Trim() -split '\s+')[0]
}

function Get-VBoxMachineReadableInfo([string]$Name) {
    $result = Invoke-VBoxManage @('showvminfo', $Name, '--machinereadable')
    $values = [ordered]@{}
    foreach ($line in ($result.Output -split "`r?`n")) {
        if ($line -match '^([^=]+)="(.*)"$') { $values[$Matches[1]] = $Matches[2] }
        elseif ($line -match '^([^=]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] }
    }
    return [pscustomobject]$values
}

function Get-VBoxVmState([string]$Name) {
    $info = Get-VBoxMachineReadableInfo $Name
    if ($null -eq $info.PSObject.Properties['VMState']) { throw "VirtualBox VM state is unavailable: $Name" }
    return [string]$info.VMState
}

function Get-VBoxVmDiskPaths([string]$Name) {
    $result = Invoke-VBoxManage @('showvminfo', $Name, '--machinereadable')
    $paths = [System.Collections.Generic.List[string]]::new()
    foreach ($line in ($result.Output -split "`r?`n")) {
        if ($line -match '^[A-Za-z]+-\d+-\d+="(?<path>.+)"$' -and $Matches.path -match '\.(vdi|vhdx|vmdk)$') { [void]$paths.Add([IO.Path]::GetFullPath($Matches.path)) }
    }
    return @($paths | Sort-Object -Unique)
}

function Get-TestLabConfig {
    param([string]$ConfigPath)
    $path = if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) { $ConfigPath } elseif ($env:SC_TESTLAB_CONFIG) { $env:SC_TESTLAB_CONFIG } else { Join-Path $PSScriptRoot 'TestLab.local.psd1' }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "TestLab configuration is not present: $path. Create a user-approved local-only VirtualBox config." }
    $config = Import-PowerShellDataFile -LiteralPath $path
    if (-not $config.ContainsKey('Root') -or [string]::IsNullOrWhiteSpace([string]$config.Root)) { throw "TestLab configuration key 'Root' is required." }
    $config.Root = [IO.Path]::GetFullPath([string]$config.Root)
    foreach ($key in @('Windows11Iso', 'Windows10Iso', 'GuestCredentialReference')) {
        $config[$key] = if ($config.ContainsKey($key) -and -not [string]::IsNullOrWhiteSpace([string]$config[$key])) { [IO.Path]::GetFullPath([string]$config[$key]) } else { $null }
    }
    return $config
}

function Test-IsProtectedTestLabPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $protected = @(
        [Environment]::GetFolderPath('MyDocuments'),
        [Environment]::GetFolderPath('CommonApplicationData'),
        [Environment]::GetFolderPath('ProgramFiles'),
        $env:WINDIR,
        (Get-Location).Path,
        $env:OneDrive,
        $env:OneDriveCommercial
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { [IO.Path]::GetFullPath([string]$_).TrimEnd('\') }
    foreach ($item in $protected) {
        if ($full.Equals($item, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($item + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Assert-TestLabRoot {
    param([Parameter(Mandatory = $true)][string]$Root)
    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw "TestLab root does not exist: $full" }
    if ($full.Equals([IO.Path]::GetPathRoot($full).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to use a volume root as TestLab root: $full" }
    if (Test-IsProtectedTestLabPath $full) { throw "Refusing a repository, OneDrive, user-document, ProgramData, Program Files, Windows, or current-directory TestLab root: $full" }
    return $full
}

function Assert-ExistingIso {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Label)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or [IO.Path]::GetExtension($Path) -ine '.iso') { throw "$Label must be an existing local .iso file: $Path" }
}

function Get-TestLabVmDefinition {
    param([Parameter(Mandatory = $true)][ValidateSet('Windows11', 'Windows10')][string]$Guest)
    if ($Guest -eq 'Windows11') { return [pscustomobject]@{ Name = 'SC-Test-W11-VBox'; TargetOs = 'Windows11'; IsoKey = 'Windows11Iso'; GuestOsType = 'Windows11_64'; DiskSizeGiB = 80; SecureBoot = $true; Tpm = $true } }
    return [pscustomobject]@{ Name = 'SC-Test-W10-VBox'; TargetOs = 'Windows10-22H2'; IsoKey = 'Windows10Iso'; GuestOsType = 'Windows10_64'; DiskSizeGiB = 64; SecureBoot = $false; Tpm = $false }
}

function Assert-ExactTestLabVm {
    param([Parameter(Mandatory = $true)][ValidateSet('SC-Test-W11-VBox', 'SC-Test-W10-VBox')][string]$Name)
    $result = Invoke-VBoxManage @('showvminfo', $Name, '--machinereadable') -AllowNonZero
    if ($result.ExitCode -ne 0) { throw "Approved VirtualBox VM does not exist: $Name" }
    return [pscustomobject]@{ Name = $Name; State = Get-VBoxVmState $Name; Info = Get-VBoxMachineReadableInfo $Name }
}

function Assert-TestLabVmDisks {
    param([Parameter(Mandatory = $true)]$Vm, [Parameter(Mandatory = $true)][string]$Root)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $paths = @(Get-VBoxVmDiskPaths $Vm.Name)
    if ($paths.Count -eq 0) { throw "The approved VirtualBox VM has no registered dynamic disk: $($Vm.Name)" }
    foreach ($path in $paths) {
        if (-not ($path.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase) -or $path.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase))) { throw "The approved VirtualBox VM disk is outside the approved root: $path" }
    }
}

function Assert-PathUnderRoot {
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$Path)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not ($pathFull.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase) -or $pathFull.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase))) { throw "Path is outside the approved TestLab root: $pathFull" }
    return $pathFull
}

function Assert-VirtualBoxResourceGate {
    param([Parameter(Mandatory = $true)][string]$Root, [switch]$RequireMftSeed)
    $os = Get-CimInstance Win32_OperatingSystem
    $freeMemoryGiB = [math]::Round($os.FreePhysicalMemory / 1MB, 2)
    $driveRoot = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Root))
    $drive = Get-PSDrive -Name $driveRoot.TrimEnd('\').TrimEnd(':') -PSProvider FileSystem -ErrorAction Stop
    $freeDiskGiB = [math]::Round($drive.Free / 1GB, 2)
    if ($freeMemoryGiB -lt 6) { throw "VirtualBox TestLab start blocked: available host memory is $freeMemoryGiB GiB (<6 GiB)." }
    if ($freeDiskGiB -lt 40) { throw "VirtualBox TestLab write blocked: TestLabRoot volume has $freeDiskGiB GiB free (<40 GiB)." }
    if ($RequireMftSeed -and $freeDiskGiB -lt 60) { throw "MFT seed creation blocked: TestLabRoot volume has $freeDiskGiB GiB free (<60 GiB)." }
    return [pscustomobject]@{ AvailableMemoryGiB = $freeMemoryGiB; FreeDiskGiB = $freeDiskGiB; Profile = if ($freeDiskGiB -ge 100) { 'full-provisioning' } elseif ($freeDiskGiB -ge 60) { 'existing-baseline-only' } else { 'functional-smoke-only' } }
}

function Assert-VBoxSafeSettings {
    param([Parameter(Mandatory = $true)][string]$Name, [switch]$AllowProvisioningNetwork)
    $info = Get-VBoxMachineReadableInfo $Name
    $expected = [ordered]@{ 'clipboard-mode' = 'disabled'; draganddrop = 'disabled'; accelerate3d = 'off'; 'audio-enabled' = 'off'; usb = 'off' }
    foreach ($property in $expected.Keys) {
        if ($null -ne $info.PSObject.Properties[$property] -and [string]$info.$property -ne $expected[$property]) { throw "VirtualBox safety setting $property is not $($expected[$property]) for $Name." }
    }
    if (-not $AllowProvisioningNetwork -and $null -ne $info.PSObject.Properties['nic1'] -and [string]$info.nic1 -notmatch '^none$') { throw "VirtualBox network is not disconnected for $Name." }
    $shared = @($info.PSObject.Properties | Where-Object Name -match 'SharedFolder')
    if ($shared.Count -gt 0) { throw "VirtualBox shared folders are configured for $Name; shared folders are prohibited." }
}

function Set-VBoxVmProvisioningSettings {
    param([Parameter(Mandatory = $true)][string]$Name, [Parameter(Mandatory = $true)][bool]$Provisioning)
    $network = if ($Provisioning) { 'nat' } else { 'none' }
    Invoke-VBoxManage @('modifyvm', $Name, '--memory', '4096', '--cpus', '2', '--firmware', 'efi', '--vram', '64', '--accelerate3d', 'off', '--audio-enabled', 'off', '--audio-driver', 'none', '--usb', 'off', '--clipboard-mode', 'disabled', '--draganddrop', 'disabled', '--nic1', $network) | Out-Null
}

function Start-TestLabVm {
    param([Parameter(Mandatory = $true)][string]$Name)
    Assert-VBoxSafeSettings $Name
    if ((Get-VBoxVmState $Name) -ne 'poweroff') { throw "VirtualBox VM must be powered off before start: $Name" }
    Invoke-VBoxManage @('startvm', $Name, '--type', 'headless') | Out-Null
}

function Stop-TestLabVm {
    param([Parameter(Mandatory = $true)][string]$Name)
    $state = Get-VBoxVmState $Name
    if ($state -eq 'poweroff') { return }
    Invoke-VBoxManage @('controlvm', $Name, 'acpipowerbutton') -AllowNonZero | Out-Null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 2
        if ((Get-VBoxVmState $Name) -eq 'poweroff') { return }
    }
    Invoke-VBoxManage @('controlvm', $Name, 'poweroff') | Out-Null
}

function Restore-TestLabBaseline {
    param([Parameter(Mandatory = $true)][string]$Name, [string]$Snapshot = 'SC-CLEAN-BASELINE')
    if ((Get-VBoxVmState $Name) -ne 'poweroff') { Stop-TestLabVm $Name }
    Invoke-VBoxManage @('snapshot', $Name, 'restore', $Snapshot) | Out-Null
}

function Ensure-TestLabBaseline {
    param([Parameter(Mandatory = $true)][string]$Name, [string]$Snapshot = 'SC-CLEAN-BASELINE')
    $result = Invoke-VBoxManage @('snapshot', $Name, 'showvminfo', $Snapshot) -AllowNonZero
    if ($result.ExitCode -ne 0) { throw "Required VirtualBox baseline snapshot does not exist: $Name/$Snapshot. Complete guest setup and create exactly one baseline snapshot with user approval." }
}

function New-TestLabArtifactDirectory {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot, [string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $path = Join-Path $RepositoryRoot "artifacts\acceptance\testlab\$RunId"
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    return $path
}

function Write-TestLabJson {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)]$Value)
    $parent = Split-Path -Parent $Path
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    $Value | ConvertTo-Json -Depth 24 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Get-TestLabGuestCredential {
    param([pscredential]$Credential, [string]$CredentialReference)
    if ($null -ne $Credential) { return $Credential }
    if ([string]::IsNullOrWhiteSpace($CredentialReference)) { throw 'A guest credential is required for VirtualBox guestcontrol and must be supplied as a local-only PSCredential reference.' }
    if (-not (Test-Path -LiteralPath $CredentialReference -PathType Leaf)) { throw "Guest credential reference does not exist: $CredentialReference" }
    $value = Import-Clixml -LiteralPath $CredentialReference
    if ($value -isnot [pscredential]) { throw 'The guest credential reference did not contain a PSCredential.' }
    return $value
}

function Invoke-VBoxGuestControl {
    param(
        [Parameter(Mandatory = $true)][string]$VmName,
        [Parameter(Mandatory = $true)][pscredential]$Credential,
        [Parameter(Mandatory = $true)][string]$Executable,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory = $true)][string]$TempRoot
    )
    $passwordFile = Join-Path $TempRoot ('.vbox-password-' + [guid]::NewGuid().ToString('N') + '.txt')
    $passwordFile = Assert-PathUnderRoot -Root $TempRoot -Path $passwordFile
    Set-Content -LiteralPath $passwordFile -Value $Credential.GetNetworkCredential().Password -Encoding UTF8 -NoNewline
    try {
        $args = @('guestcontrol', $VmName, 'run', '--exe', $Executable, '--username', $Credential.UserName, '--passwordfile', $passwordFile, '--wait-exit', '--wait-stdout', '--wait-stderr', '--') + @($Arguments)
        return Invoke-VBoxManage $args
    } finally {
        if (Test-Path -LiteralPath $passwordFile) { Remove-Item -LiteralPath $passwordFile -Force -ErrorAction SilentlyContinue }
    }
}

function Wait-TestLabGuestReady {
    param([Parameter(Mandatory = $true)][string]$VmName, [Parameter(Mandatory = $true)][pscredential]$Credential, [Parameter(Mandatory = $true)][string]$TempRoot, [int]$TimeoutSeconds = 180)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $probe = Invoke-VBoxGuestControl -VmName $VmName -Credential $Credential -Executable 'C:\Windows\System32\cmd.exe' -Arguments @('/c', 'exit', '0') -TempRoot $TempRoot
            if ($probe.ExitCode -eq 0) { return }
        } catch { }
        Start-Sleep -Seconds 3
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "VirtualBox guest did not become ready within $TimeoutSeconds seconds: $VmName"
}

function Copy-TestArtifactToVm {
    param([Parameter(Mandatory = $true)][string]$VmName, [Parameter(Mandatory = $true)][pscredential]$Credential, [Parameter(Mandatory = $true)][string]$SourcePath, [Parameter(Mandatory = $true)][string]$GuestDirectory, [Parameter(Mandatory = $true)][string]$TempRoot)
    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) { throw "Host artifact does not exist: $SourcePath" }
    $passwordFile = Assert-PathUnderRoot -Root $TempRoot -Path (Join-Path $TempRoot ('.vbox-password-' + [guid]::NewGuid().ToString('N') + '.txt'))
    Set-Content -LiteralPath $passwordFile -Value $Credential.GetNetworkCredential().Password -Encoding UTF8 -NoNewline
    try { Invoke-VBoxManage @('guestcontrol', $VmName, 'copyto', [IO.Path]::GetFullPath($SourcePath), '--target-directory', $GuestDirectory, '--username', $Credential.UserName, '--passwordfile', $passwordFile) | Out-Null }
    finally { if (Test-Path -LiteralPath $passwordFile) { Remove-Item -LiteralPath $passwordFile -Force -ErrorAction SilentlyContinue } }
}

function Copy-TestArtifactFromVm {
    param([Parameter(Mandatory = $true)][string]$VmName, [Parameter(Mandatory = $true)][pscredential]$Credential, [Parameter(Mandatory = $true)][string]$GuestPath, [Parameter(Mandatory = $true)][string]$HostDirectory, [Parameter(Mandatory = $true)][string]$TempRoot)
    New-Item -ItemType Directory -Force -Path $HostDirectory | Out-Null
    $passwordFile = Assert-PathUnderRoot -Root $TempRoot -Path (Join-Path $TempRoot ('.vbox-password-' + [guid]::NewGuid().ToString('N') + '.txt'))
    Set-Content -LiteralPath $passwordFile -Value $Credential.GetNetworkCredential().Password -Encoding UTF8 -NoNewline
    try { Invoke-VBoxManage @('guestcontrol', $VmName, 'copyfrom', $GuestPath, '--target-directory', [IO.Path]::GetFullPath($HostDirectory), '--username', $Credential.UserName, '--passwordfile', $passwordFile) | Out-Null }
    finally { if (Test-Path -LiteralPath $passwordFile) { Remove-Item -LiteralPath $passwordFile -Force -ErrorAction SilentlyContinue } }
}

function Assert-VirtualBoxHostPrerequisites {
    param([Parameter(Mandatory = $true)][string]$Root, [string]$Windows11Iso, [string]$Windows10Iso)
    $rootFull = Assert-TestLabRoot $Root
    $version = Get-VBoxVersion
    if ($version -notmatch '^7\.2\.') { throw "Unsupported VirtualBox version '$version'. Only 7.2.x is accepted; 7.2.16 is the baseline." }
    $hostInfo = Invoke-VBoxManage @('list', 'hostinfo')
    $extensionPacks = Invoke-VBoxManage @('list', 'extpacks')
    if ($extensionPacks.Output -notmatch '(?im)^Extension Packs:\s+0\s*$') { throw 'VirtualBox Extension Pack is installed or could not be proven absent; this TestLab requires no Extension Pack.' }
    $os = Get-CimInstance Win32_OperatingSystem
    $computer = Get-CimInstance Win32_ComputerSystem
    $drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($rootFull).TrimEnd('\').TrimEnd(':')) -PSProvider FileSystem -ErrorAction Stop
    $freeDiskGiB = [math]::Round($drive.Free / 1GB, 2)
    $freeMemoryGiB = [math]::Round($os.FreePhysicalMemory / 1MB, 2)
    $hostCapability = [bool]($hostInfo.Output -match '(?im)VT-x/AMD-V:\s+enabled')
    $ready = $freeDiskGiB -ge 40 -and $freeMemoryGiB -ge 6 -and $hostCapability
    foreach ($iso in @(@{ Name = 'Windows11Iso'; Path = $Windows11Iso }, @{ Name = 'Windows10Iso'; Path = $Windows10Iso })) { if (-not [string]::IsNullOrWhiteSpace([string]$iso.Path)) { Assert-ExistingIso -Path $iso.Path -Label $iso.Name } }
    return [ordered]@{
        Schema = 'StorageChronicle.VirtualBoxHostPreflight.v1'; GeneratedUtc = [DateTimeOffset]::UtcNow; ProductName = $os.Caption; OsVersion = $os.Version; OsBuild = $os.BuildNumber; Architecture = $os.OSArchitecture; Cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name); Cores = $computer.NumberOfLogicalProcessors; TotalMemoryGiB = [math]::Round($computer.TotalPhysicalMemory / 1GB, 2); AvailableMemoryGiB = $freeMemoryGiB; TestLabRoot = $rootFull; FreeDiskGiB = $freeDiskGiB; FileSystem = (Get-Volume -DriveLetter ([IO.Path]::GetPathRoot($rootFull).Substring(0,1))).FileSystem; VirtualBoxPath = Get-VBoxManagePath; VirtualBoxVersion = $version; VirtualBoxBaseline = '7.2.16'; VirtualBoxVersionPolicy = '7.2.16 baseline; same-series 7.2.x fallback requires reporting'; ExtensionPacks = $extensionPacks.Output; HostInfo = $hostInfo.Output; HostVirtualizationCapability = $hostCapability; WmiVirtualizationFirmwareEnabled = [bool](Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty VirtualizationFirmwareEnabled); HyperVModulePresent = [bool](Get-Module -ListAvailable -Name Hyper-V); Windows11Iso = $Windows11Iso; Windows10Iso = $Windows10Iso; ReadyForProvisioning = $ready; ReadyForFunctionalAcceptance = $ready; ResourceProfile = if ($freeDiskGiB -ge 100) { 'full-provisioning' } elseif ($freeDiskGiB -ge 60) { 'existing-baseline-only' } else { 'functional-smoke-only' }
    }
}
