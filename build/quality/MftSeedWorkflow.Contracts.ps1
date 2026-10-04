Set-StrictMode -Version Latest

function Get-MftSeedWorkflowHostInventory {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
    $computer = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
    $systemDrive = [string]$env:SystemDrive
    $systemVolume = Get-Volume -DriveLetter $systemDrive.Substring(0, 1) -ErrorAction Stop
    $allDisks = @(Get-Disk -ErrorAction Stop)
    $protectedDisks = @($allDisks | Where-Object { $_.IsSystem -or $_.IsBoot } | ForEach-Object { [string]$_.UniqueId } | Sort-Object -Unique)
    $allVolumes = @(Get-Volume -ErrorAction Stop)
    $protectedVolumeIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($volume in $allVolumes) {
        if ($volume.IsSystem -or $volume.IsBoot -or [string]$volume.DriveType -eq 'CD-ROM' -or [string]$volume.DriveLetter -ieq $systemDrive.TrimEnd(':')) { [void]$protectedVolumeIds.Add([string]$volume.UniqueId) }
    }
    foreach ($disk in $allDisks) {
        foreach ($partition in @(Get-Partition -DiskNumber ([int]$disk.Number) -ErrorAction Stop)) {
            if ([string]$partition.Type -match 'Recovery|System|Reserved|EFI') {
                foreach ($volume in @(Get-Volume -Partition $partition -ErrorAction Stop)) { [void]$protectedVolumeIds.Add([string]$volume.UniqueId) }
            }
        }
    }
    $pageFilePaths = @(Get-CimInstance -ClassName Win32_PageFileUsage -ErrorAction Stop | ForEach-Object { [string]$_.Name })
    $crash = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' -ErrorAction Stop
    $protectedPaths = @($pageFilePaths + [string]$crash.DumpFile + [string]$crash.MinidumpDir) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
    foreach ($protectedPath in $protectedPaths) {
        $expanded = [Environment]::ExpandEnvironmentVariables([string]$protectedPath)
        if ($expanded -match '^(?:\\\?\\)?(?<drive>[A-Za-z]):\\') {
            $protectedVolume = @($allVolumes | Where-Object { [string]$_.DriveLetter -ieq $Matches.drive })
            foreach ($volume in $protectedVolume) { [void]$protectedVolumeIds.Add([string]$volume.UniqueId) }
        } else { throw "A pagefile/crash-dump location cannot be resolved to a local volume: $protectedPath" }
    }
    [pscustomobject]@{
        ComputerName = [string]$env:COMPUTERNAME
        OperatingSystem = [string]$os.Caption
        OsBuild = [string]$os.BuildNumber
        SystemDrive = $systemDrive
        SystemVolumeUniqueId = [string]$systemVolume.UniqueId
        IsPhysicalMachine = ([string]$computer.Model + ' ' + [string]$computer.Manufacturer) -notmatch '(?i)virtual|vmware|virtualbox|qemu|kvm|xen|parallels|amazon ec2|google compute|openstack'
        ProtectedDiskUniqueIds = $protectedDisks
        ProtectedVolumeUniqueIds = @($protectedVolumeIds | Sort-Object)
        ProtectedVolumeRoots = @($allVolumes | Where-Object { [string]$_.UniqueId -in $protectedVolumeIds } | ForEach-Object { if ($_.DriveLetter) { [string]$_.DriveLetter + ':\' } })
        PageFilePaths = $pageFilePaths
        CrashDumpPath = [string]$crash.DumpFile
        MiniDumpPath = [string]$crash.MinidumpDir
        CapturedUtc = [DateTimeOffset]::UtcNow
    }
}

function Get-MftSeedWorkflowPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ApprovedRoot,
        [Parameter(Mandatory = $true)][string]$EvidenceParent,
        [Parameter(Mandatory = $true)][guid]$RunId,
        [ValidateRange(64, 256)][int]$VhdxSizeGiB = 96
    )
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'MFT seed creation is Windows-only.' }
    if ($RunId -eq [guid]::Empty) { throw 'RunId must be a non-empty GUID.' }
    $root = Assert-MftWorkflowDirectory -Path $ApprovedRoot -ParameterName 'ApprovedRoot'
    $evidenceParentFull = Assert-MftWorkflowDirectory -Path $EvidenceParent -ParameterName 'EvidenceParent'
    if ([string]::Equals($root, $evidenceParentFull, [StringComparison]::OrdinalIgnoreCase) -or $root.StartsWith($evidenceParentFull.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $evidenceParentFull.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ApprovedRoot and EvidenceParent must be separate, non-nested directories.'
    }
    if (@(Get-ChildItem -LiteralPath $root -Force -ErrorAction Stop).Count -ne 0) { throw 'ApprovedRoot must already exist and be empty; no existing content may be adopted or changed.' }
    $hostVolume = Get-Volume -DriveLetter ([IO.Path]::GetPathRoot($root).Substring(0, 1)) -ErrorAction Stop
    Assert-MftWorkflowHostVolume -Volume $hostVolume -Path $root
    $evidenceVolume = Get-Volume -DriveLetter ([IO.Path]::GetPathRoot($evidenceParentFull).Substring(0, 1)) -ErrorAction Stop
    Assert-MftWorkflowHostVolume -Volume $evidenceVolume -Path $evidenceParentFull
    if ([int64]$hostVolume.SizeRemaining -lt 60GB) { throw 'The host volume must have at least 60 GiB free before MFT seed creation.' }
    $hostInventory = Get-MftSeedWorkflowHostInventory
    if (-not [bool]$hostInventory.IsPhysicalMachine) { throw 'MFT seed creation requires a physical Windows host; OS-wide virtual machines are not accepted.' }
    $runName = $RunId.ToString('D')
    $evidenceRoot = Join-Path $evidenceParentFull ('mft-seed-' + $runName)
    $seedDirectory = Join-Path $root $runName
    if (Test-Path -LiteralPath $evidenceRoot -or Test-Path -LiteralPath $seedDirectory) { throw 'A run-scoped evidence or seed directory already exists; refusing reuse.' }
    $vhdxPath = Join-Path $seedDirectory ('MftSeed-' + $runName + '.vhdx')
    if (Test-Path -LiteralPath $vhdxPath) { throw 'The run-GUID VHDX path already exists; refusing overwrite.' }
    [pscustomobject]@{
        RunId = $runName
        ApprovedRoot = $root
        RootDriveLetter = [IO.Path]::GetPathRoot($root).Substring(0, 1)
        EvidenceParent = $evidenceParentFull
        RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
        EvidenceRoot = $evidenceRoot
        SeedDirectory = $seedDirectory
        VhdxPath = $vhdxPath
        VhdxSizeGiB = $VhdxSizeGiB
        VhdxType = 'Dynamic'
        VolumeLabel = 'SC_TEST_MFT_VOLUME'
        DatasetEntryCount = 1000000
        IntentPath = Join-Path $evidenceRoot 'run-intent.json'
        CreationEvidencePath = Join-Path $evidenceRoot 'seed-creation.json'
        HostFreeGiB = [math]::Floor([double]$hostVolume.SizeRemaining / 1GB)
        ApprovedRootVolumeUniqueId = [string]$hostVolume.UniqueId
        EvidenceVolumeUniqueId = [string]$evidenceVolume.UniqueId
        HostInventory = $hostInventory
        WorkloadDescription = 'StorageChronicle.FileMutationWorkload --scenario mft --count 1000000 (zero-byte namespace workload)'
        FormatTarget = 'Only the new, attached, re-identified file-backed VHDX partition; NTFS label SC_TEST_MFT_VOLUME.'
        ApplyCommandExample = ('pwsh -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $PSScriptRoot 'New-MftPhysicalSeed.ps1') + '" -ApprovedRoot "' + $root + '" -EvidenceParent "' + $evidenceParentFull + '" -RunId ' + $runName + ' -VhdxSizeGiB ' + $VhdxSizeGiB + ' -Apply -ConfirmationToken "CREATE MFT SEED ' + $runName + '"')
        ExternalMonitorRequirement = 'A separately collected process-attributed write-monitor evidence file is mandatory; creator-authored intent/manifest is not independent evidence.'
    }
}

function Assert-MftWorkflowDirectory {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$ParameterName)
    $full = [IO.Path]::GetFullPath($Path)
    $volumeRoot = [IO.Path]::GetPathRoot($full)
    if ($full.TrimEnd('\').Equals($volumeRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { throw "$ParameterName cannot be a volume root: $full" }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw "$ParameterName must be an existing user-approved directory: $full" }
    if ($full.StartsWith('\\', [StringComparison]::Ordinal) -or $full.StartsWith('\\?\', [StringComparison]::Ordinal) -or $full.StartsWith('\\.\', [StringComparison]::Ordinal)) { throw "$ParameterName cannot be a UNC or device path." }
    if ($full -notmatch '^(?<drive>[A-Za-z]):\\') { throw "$ParameterName must be on a local drive-letter volume." }
    $drive = $Matches.drive
    $volume = Get-Volume -DriveLetter $drive -ErrorAction Stop
    if ([string]$volume.DriveType -ne 'Fixed' -or [string]$volume.FileSystem -ne 'NTFS' -or -not [bool]$volume.IsReady) { throw "$ParameterName must be on a ready, local fixed NTFS volume." }
    Assert-MftWorkflowNoReparsePath -Path $full
    Assert-MftWorkflowOutsideRepository -Path $full
    $knownSyncPaths = @($env:OneDrive, $env:OneDriveConsumer, $env:OneDriveCommercial, (Join-Path $env:USERPROFILE 'OneDrive'), (Join-Path $env:USERPROFILE 'Dropbox'), (Join-Path $env:USERPROFILE 'Google Drive')) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
    foreach ($syncPath in $knownSyncPaths) {
        $syncFull = [IO.Path]::GetFullPath([string]$syncPath).TrimEnd('\')
        if ($full.Equals($syncFull, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($syncFull + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "$ParameterName is inside a known synchronized directory: $syncFull" }
    }
    if ($full -match '(?i)(?:\\OneDrive(?:\\|$)|\\Dropbox(?:\\|$)|\\Google Drive(?:\\|$))') { throw "$ParameterName appears to be inside a synchronized directory." }
    $documents = [IO.Path]::GetFullPath((Join-Path $env:USERPROFILE 'Documents')).TrimEnd('\')
    if ($full.Equals($documents, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($documents + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "$ParameterName cannot be inside Documents." }
    return $full
}

function Assert-MftWorkflowNoReparsePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point in approved path: $current" }
            $cloudRecallAttribute = [enum]::ToObject([IO.FileAttributes], 0x00400000)
            if (($item.Attributes -band ([IO.FileAttributes]::Offline -bor $cloudRecallAttribute)) -ne 0) { throw "Cloud/sparse recall path is not accepted for an isolated root: $current" }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent -or $parent.FullName -eq $current) { break }
        $current = $parent.FullName
    }
}

function Assert-MftWorkflowHostVolume {
    param([Parameter(Mandatory = $true)]$Volume, [Parameter(Mandatory = $true)][string]$Path)
    if ([string]$Volume.DriveType -ne 'Fixed' -or [string]$Volume.FileSystem -ne 'NTFS' -or -not [bool]$Volume.IsReady) { throw "The host path must resolve to a ready fixed NTFS volume: $Path" }
    Assert-MftWorkflowProtectedVolume -VolumeUniqueId ([string]$Volume.UniqueId) -Inventory (Get-MftSeedWorkflowHostInventory)
}

function Assert-MftWorkflowProtectedVolume {
    param([Parameter(Mandatory = $true)][string]$VolumeUniqueId, [Parameter(Mandatory = $true)]$Inventory)
    if ([string]$VolumeUniqueId -in @($Inventory.ProtectedVolumeUniqueIds)) { throw 'The approved root/evidence path resolves to a system, boot, recovery, pagefile, crash-dump, or protected volume.' }
}

function Assert-MftWorkflowOutsideRepository {
    param([Parameter(Mandatory = $true)][string]$Path)
    $repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).TrimEnd('\')
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($repositoryRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'ApprovedRoot/EvidenceParent cannot be inside the repository.' }
}

function Assert-MftExternalArtifactRoot {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)
    $full = Assert-MftWorkflowDirectory -Path $Path -ParameterName 'ArtifactRoot'
    $volume = Get-Volume -DriveLetter ([IO.Path]::GetPathRoot($full).Substring(0, 1)) -ErrorAction Stop
    Assert-MftWorkflowProtectedVolume -VolumeUniqueId ([string]$volume.UniqueId) -Inventory (Get-MftSeedWorkflowHostInventory)
    return $full
}

function Get-MftWorkflowAvailableDriveLetter {
    $occupied = @(Get-Volume -ErrorAction Stop | Where-Object { $_.DriveLetter } | ForEach-Object { [string]$_.DriveLetter })
    foreach ($letter in 'D','E','F','G','H','I','J','K','L','M','N','O','P','Q','R','S','T','U','V','W','X','Y','Z') {
        if ($letter -notin $occupied -and -not (Test-Path -LiteralPath ($letter + ':\'))) { return $letter }
    }
    throw 'No unused drive letter is available for the new seed volume.'
}

function Assert-MftWorkflowNewRawDisk {
    param([Parameter(Mandatory = $true)]$Disk, [Parameter(Mandatory = $true)][string]$VhdPath, [Parameter(Mandatory = $true)]$Vhd, [switch]$AllowInitialized)
    if ([IO.Path]::GetFullPath([string]$Vhd.Path) -ne [IO.Path]::GetFullPath($VhdPath) -or -not [bool]$Vhd.Attached) { throw 'Attached disk path does not match the newly created VHDX.' }
    if ([int]$Vhd.DiskNumber -ne [int]$Disk.Number -or [string]$Disk.BusType -notmatch 'File Backed Virtual|FileBackedVirtual') { throw 'Disk number or bus type does not identify the VHDX file-backed disk.' }
    if ([bool]$Disk.IsSystem -or [bool]$Disk.IsBoot -or [bool]$Disk.IsOffline -or [bool]$Disk.IsReadOnly -or [string]$Disk.OperationalStatus -notmatch 'Online') { throw 'The selected disk is protected, offline, read-only, or not online.' }
    $protectedDiskIds = @(Get-MftSeedWorkflowHostInventory).ProtectedDiskUniqueIds
    if ([string]$Disk.UniqueId -in $protectedDiskIds) { throw 'The VHDX disk identity collides with a protected host disk.' }
    if ($AllowInitialized) {
        if ([string]$Disk.PartitionStyle -ne 'GPT') { throw 'The initialized VHDX did not produce the expected GPT disk.' }
    } elseif ([string]$Disk.PartitionStyle -ne 'RAW') { throw 'Only a new RAW VHDX disk may be initialized.' }
}

function Assert-MftWorkflowPlanStorageRoots {
    param([Parameter(Mandatory = $true)]$Plan)
    foreach ($target in @(@([string]$Plan.ApprovedRoot, [string]$Plan.ApprovedRootVolumeUniqueId), @([string]$Plan.EvidenceParent, [string]$Plan.EvidenceVolumeUniqueId))) {
        Assert-MftWorkflowNoReparsePath -Path $target[0]
        Assert-MftWorkflowOutsideRepository -Path $target[0]
        $volume = Get-Volume -DriveLetter ([IO.Path]::GetPathRoot($target[0]).Substring(0, 1)) -ErrorAction Stop
        if ([string]$volume.UniqueId -ne [string]$target[1]) { throw "Approved storage root volume identity changed: $($target[0])" }
        Assert-MftWorkflowHostVolume -Volume $volume -Path $target[0]
    }
}

function Assert-MftWorkflowMountedSeedIdentity {
    param(
        [Parameter(Mandatory = $true)][string]$VhdxPath,
        [Parameter(Mandatory = $true)][string]$VhdxFileIdentity,
        [Parameter(Mandatory = $true)][string]$DiskUniqueId,
        [Parameter(Mandatory = $true)][string]$DriveLetter,
        [Parameter(Mandatory = $true)][string]$VolumeUniqueId,
        [Parameter(Mandatory = $true)][string]$VolumeLabel
    )
    $vhd = Get-VHD -Path $VhdxPath -ErrorAction Stop
    if (-not [bool]$vhd.Attached -or [string]$vhd.VhdType -ne 'Dynamic' -or [IO.Path]::GetFullPath([string]$vhd.Path) -ne [IO.Path]::GetFullPath($VhdxPath) -or (Get-MftWindowsFileIdentity -Path $VhdxPath) -ne $VhdxFileIdentity) { throw 'Mounted VHDX path/file identity changed.' }
    $disk = Get-Disk -Number ([int]$vhd.DiskNumber) -ErrorAction Stop
    if ([string]$disk.UniqueId -ne $DiskUniqueId -or [string]$disk.BusType -notmatch 'File Backed Virtual|FileBackedVirtual' -or [bool]$disk.IsSystem -or [bool]$disk.IsBoot -or [bool]$disk.IsOffline -or [bool]$disk.IsReadOnly) { throw 'Mounted VHDX disk unique ID or safe-role attributes changed.' }
    if ([string]$disk.UniqueId -in @(Get-MftSeedWorkflowHostInventory).ProtectedDiskUniqueIds) { throw 'Mounted seed disk matches a protected system or boot disk.' }
    $volume = Get-Volume -DriveLetter $DriveLetter -ErrorAction Stop
    if ([string]$volume.UniqueId -ne $VolumeUniqueId -or [string]$volume.FileSystem -ne 'NTFS' -or [string]$volume.FileSystemLabel -ne $VolumeLabel -or [string]$volume.UniqueId -in @(Get-MftSeedWorkflowHostInventory).ProtectedVolumeUniqueIds) { throw 'Mounted seed volume identity, filesystem, label, or protected-role classification changed.' }
    return [pscustomobject]@{ Vhd = $vhd; Disk = $disk; Volume = $volume }
}

function Test-MftWorkflowAdministrator {
    if (-not [Environment]::OSVersion.Platform.Equals([PlatformID]::Win32NT)) { return $false }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function New-MftSeedEvidenceRun {
    param([Parameter(Mandatory = $true)]$Plan)
    New-MftDirectoryCreateNew -Path ([string]$Plan.EvidenceRoot) | Out-Null
    Assert-MftWorkflowNoReparsePath -Path ([string]$Plan.EvidenceRoot)
    return [string]$Plan.EvidenceRoot
}

function New-MftDirectoryCreateNew {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [scriptblock]$CreateDirectoryAction
    )
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($CreateDirectoryAction) {
        # Injectable only for offline contract tests. A false result represents a
        # Win32 create-new collision/failure and is never treated as success.
        $created = & $CreateDirectoryAction $fullPath
        if (-not [bool]$created) { throw "Atomic create-new directory operation failed: $fullPath" }
    } else {
        if (-not ('StorageChronicle.MftDirectoryCreator' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace StorageChronicle {
  public static class MftDirectoryCreator {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true, EntryPoint="CreateDirectoryW")]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);
    public static void CreateNew(string path) {
      if (!CreateDirectory(path, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDirectoryW create-new failed: " + path);
    }
  }
}
'@ -ErrorAction Stop | Out-Null
        }
        [StorageChronicle.MftDirectoryCreator]::CreateNew($fullPath)
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) { throw "Atomic create-new did not produce a directory: $fullPath" }
    Assert-MftWorkflowNoReparsePath -Path $fullPath
    return $fullPath
}

function New-MftVhdxCreateNew {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$SizeBytes,
        [Parameter(Mandatory = $true)][scriptblock]$CreateAction
    )
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $fullPath) { throw "Create-new VHDX target already exists: $fullPath" }
    # The create action is injected so collision/failure behavior can be tested
    # without invoking Hyper-V or touching a host storage device.
    & $CreateAction $fullPath $SizeBytes
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "VHDX create action returned without producing its target: $fullPath" }
    return $fullPath
}

function Write-MftCreateNewJson {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)]$Value)
    $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    Assert-MftWorkflowNoReparsePath -Path $parent
    if (Test-Path -LiteralPath $Path) { throw "Create-new evidence path already exists; refusing to overwrite: $Path" }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Value -Depth 16))
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}

function Get-MftSha256 {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)
    (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
}

function ConvertTo-MftProcessArgumentLine {
    param([Parameter(Mandatory = $true)][string[]]$Argument)
    (@($Argument | ForEach-Object { ConvertTo-MftWindowsCommandLineArgument -Argument ([string]$_) }) -join ' ')
}

function ConvertTo-MftWindowsCommandLineArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Argument)
    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append([char]'"')
    $backslashCount = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq [char]'\') { $backslashCount++; continue }
        if ($character -eq [char]'"') {
            for ($index = 0; $index -lt (2 * $backslashCount + 1); $index++) { [void]$builder.Append([char]'\') }
            [void]$builder.Append([char]'"')
            $backslashCount = 0
            continue
        }
        for ($index = 0; $index -lt $backslashCount; $index++) { [void]$builder.Append([char]'\') }
        [void]$builder.Append($character)
        $backslashCount = 0
    }
    for ($index = 0; $index -lt (2 * $backslashCount); $index++) { [void]$builder.Append([char]'\') }
    [void]$builder.Append([char]'"')
    return $builder.ToString()
}

function Get-MftWindowsFileIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not ('StorageChronicle.MftFileIdentity' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace StorageChronicle {
  public static class MftFileIdentity {
    [StructLayout(LayoutKind.Sequential)] private struct BY_HANDLE_FILE_INFORMATION {
      public uint FileAttributes; public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
      public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime; public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
      public uint VolumeSerialNumber; public uint FileSizeHigh; public uint FileSizeLow; public uint NumberOfLinks;
      public uint FileIndexHigh; public uint FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);
    public static string Read(string path) {
      using (var handle=CreateFile(path, 0x80000000, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)) {
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        BY_HANDLE_FILE_INFORMATION i; if (!GetFileInformationByHandle(handle, out i)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return i.VolumeSerialNumber.ToString("x8")+":"+i.FileIndexHigh.ToString("x8")+i.FileIndexLow.ToString("x8");
      }
    }
  }
}
'@ -ErrorAction Stop | Out-Null
    }
    [StorageChronicle.MftFileIdentity]::Read([IO.Path]::GetFullPath($Path))
}
