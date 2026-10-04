Set-StrictMode -Version Latest

function Assert-MftPhysicalSeedInventory {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Inventory)

    function Require-Equal([string]$Name, [string]$Actual, [string]$Expected) {
        if ([string]::IsNullOrWhiteSpace($Actual) -or
            -not [string]::Equals($Actual, $Expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw "MFT seed identity mismatch for $Name."
        }
    }

    $seed = $Inventory.Seed
    $disk = $Inventory.Disk
    $volume = $Inventory.Volume
    $marker = $Inventory.Marker
    if (-not [bool]$Inventory.IsWindows -or -not [bool]$Inventory.IsPhysicalMachine) { throw 'MFT acceptance requires a physical Windows host.' }
    if ([string]$Inventory.OperatingSystem -notmatch 'Windows') { throw 'The host operating system is not Windows.' }
    if ([string]$Inventory.DevicePath -notmatch '^\\\\\.\\([A-Z]):$') { throw 'The MFT device path must be an exact local drive-letter device path.' }
    if ([string]$Inventory.DevicePath -match '(?i)^\\\\\.\\C:') { throw 'C: is never an MFT benchmark target.' }
    if (-not [bool]$seed.Attached -or [string]$seed.VhdFormat -ne 'VHDX' -or [string]$seed.VhdType -ne 'Dynamic') { throw 'The seed must be an attached, file-backed dynamic VHDX.' }
    if ([int]$seed.DiskNumber -ne [int]$disk.Number) { throw 'The attached VHDX disk number does not match the selected disk.' }
    Require-Equal 'disk unique ID' ([string]$disk.UniqueId) ([string]$marker.DiskUniqueId)
    if ([bool]$disk.IsSystem -or [bool]$disk.IsBoot -or [string]$disk.BusType -notmatch 'File Backed Virtual|FileBackedVirtual') { throw 'The MFT disk is system/boot or is not a file-backed virtual disk.' }
    if ([string]$disk.OperationalStatus -notmatch 'Online') { throw 'The MFT disk is not online.' }
    $expectedDevicePath = '\\.\' + [string]$volume.DriveLetter + ':'
    if (-not [string]::Equals([string]$Inventory.DevicePath, $expectedDevicePath, [StringComparison]::OrdinalIgnoreCase)) { throw 'The device path does not identify the selected volume drive letter.' }
    if ([string]$volume.FileSystem -ne 'NTFS' -or [string]$volume.FileSystemLabel -ne 'SC_TEST_MFT_VOLUME') { throw 'The target must be the dedicated SC_TEST_MFT_VOLUME NTFS volume.' }
    if ([bool]$volume.IsSystem -or [bool]$volume.IsBoot -or [bool]$volume.IsRecovery -or [bool]$volume.IsPageFile -or [bool]$volume.IsCrashDump) { throw 'System, boot, recovery, pagefile, and crash-dump volumes are not valid MFT targets.' }
    Require-Equal 'volume unique ID' ([string]$volume.UniqueId) ([string]$marker.VolumeUniqueId)
    Require-Equal 'volume GUID path' ([string]$volume.VolumeGuidPath) ([string]$marker.VolumeGuidPath)
    Require-Equal 'marker run GUID' ([string]$marker.RunId) ([string]$seed.RunId)
    if ($marker.RunId -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') { throw 'The seed marker must contain a valid persistent run GUID.' }
    if ([string]$marker.Schema -ne 'StorageChronicle.MftSeedMarker.v1' -or
        [string]$marker.Role -ne 'MftSeed' -or
        [string]$marker.FileSystem -ne 'NTFS' -or
        [string]$marker.VolumeLabel -ne 'SC_TEST_MFT_VOLUME') { throw 'The persistent MFT seed marker schema or role is invalid.' }
    Require-Equal 'VHDX path' ([IO.Path]::GetFullPath([string]$seed.Path)) ([IO.Path]::GetFullPath([string]$marker.VhdxPath))
    if ([IO.Path]::GetFileName([IO.Path]::GetDirectoryName([string]$seed.Path)) -ne [string]$marker.RunId) { throw 'The VHDX must be in its run-GUID-named isolation directory.' }
    if ([string]::IsNullOrWhiteSpace([string]$Inventory.MarkerPath) -or
        -not [string]::Equals([IO.Path]::GetPathRoot([IO.Path]::GetFullPath([string]$Inventory.MarkerPath)), [string]$volume.DriveRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'The marker is not on the verified MFT seed volume.' }
    if ([int64]$marker.DatasetEntryCount -lt 1000000) { throw 'The persistent seed marker does not declare at least one million dataset entries.' }
    if ([string]$Inventory.DevicePath -match '(?i)^\\\\\.\\C:' -or [string]$Inventory.SystemDrive -ieq ([string]$volume.DriveLetter + ':')) { throw 'The Windows system drive cannot be an MFT benchmark target.' }
    if (@($Inventory.ProtectedVolumeUniqueIds | Where-Object { [string]::Equals([string]$_, [string]$volume.UniqueId, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) { throw 'The target volume hosts a pagefile, system, boot, recovery, or crash-dump path.' }

    return [pscustomobject]@{
        IsPhysicalMachine = $true
        OperatingSystem = [string]$Inventory.OperatingSystem
        OsBuild = [string]$Inventory.OsBuild
        CpuName = [string]$Inventory.CpuName
        CpuLogicalCount = [int]$Inventory.CpuLogicalCount
        MemoryMiB = [int64]$Inventory.MemoryMiB
        VhdxPath = [IO.Path]::GetFullPath([string]$seed.Path)
        VhdxType = [string]$seed.VhdType
        VhdxSizeGiB = [math]::Round([double]$seed.SizeBytes / 1GB, 2)
        DiskNumber = [int]$disk.Number
        DiskUniqueId = [string]$disk.UniqueId
        VolumeUniqueId = [string]$volume.UniqueId
        VolumeGuidPath = [string]$volume.VolumeGuidPath
        VolumeLabel = [string]$volume.FileSystemLabel
        DevicePath = [string]$Inventory.DevicePath
        MarkerPath = [IO.Path]::GetFullPath([string]$Inventory.MarkerPath)
        RunId = [string]$marker.RunId
        DatasetEntryCount = [int64]$marker.DatasetEntryCount
    }
}

function Get-MftPhysicalSeedInventory {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$MarkerPath, [Parameter(Mandatory = $true)][string]$DevicePath)

    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'MFT physical acceptance is Windows-only.' }
    if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf)) { throw 'The persistent MFT seed marker does not exist.' }
    $markerFullPath = [IO.Path]::GetFullPath($MarkerPath)
    $markerItem = Get-Item -LiteralPath $markerFullPath -Force
    if (($markerItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The seed marker cannot be a reparse point.' }
    $marker = Get-Content -LiteralPath $markerFullPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $vhdxPath = [IO.Path]::GetFullPath([string]$marker.VhdxPath)
    if ([IO.Path]::GetExtension($vhdxPath) -ine '.vhdx' -or -not (Test-Path -LiteralPath $vhdxPath -PathType Leaf)) { throw 'The marker VHDX path is missing or is not a VHDX file.' }

    $vhd = Get-VHD -Path $vhdxPath -ErrorAction Stop
    $disk = Get-Disk -Number ([int]$vhd.DiskNumber) -ErrorAction Stop
    $partitions = @(Get-Partition -DiskNumber ([int]$disk.Number) -ErrorAction Stop)
    $volumes = @($partitions | ForEach-Object { Get-Volume -Partition $_ -ErrorAction Stop })
    $driveMatch = [regex]::Match($DevicePath, '^\\\\\.\\([A-Z]):$', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $driveMatch.Success) { throw 'STORAGE_CHRONICLE_MFT_VOLUME must be an exact local device path such as \\.\D:.' }
    $driveLetter = $driveMatch.Groups[1].Value.ToUpperInvariant()
    $volume = @($volumes | Where-Object { [string]$_.DriveLetter -ieq $driveLetter })
    if ($volume.Count -ne 1) { throw 'The device drive letter does not resolve to exactly one volume on the attached VHDX disk.' }
    $volume = $volume[0]
    $volumePath = [string]$volume.Path
    if ([string]::IsNullOrWhiteSpace($volumePath) -or $volumePath -notmatch '^\\\\\?\\Volume\{[0-9a-fA-F-]+\}\\$') { throw 'Windows did not provide an authoritative volume GUID path.' }

    $computer = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
    $processor = Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop | Select-Object -First 1
    $model = [string]$computer.Model
    if ([string]::IsNullOrWhiteSpace($model) -or [string]::IsNullOrWhiteSpace([string]$computer.Manufacturer)) { throw 'The SMBIOS manufacturer/model needed for host classification is unavailable.' }
    $physical = ($model + ' ' + [string]$computer.Manufacturer) -notmatch '(?i)virtual|vmware|virtualbox|qemu|kvm|xen|parallels|amazon ec2|google compute|openstack'
    $pageFileRoots = [System.Collections.Generic.List[string]]::new()
    $crashDumpRoots = [System.Collections.Generic.List[string]]::new()
    function Add-ProtectedPathRoot([System.Collections.Generic.List[string]]$Collection, [string]$Path, [string]$Kind) {
        if ([string]::IsNullOrWhiteSpace($Path)) { return }
        $expanded = [Environment]::ExpandEnvironmentVariables($Path)
        $match = [regex]::Match($expanded, '^(?:\\\\\?\\|\\\?\\)?(?<drive>[A-Za-z]):\\')
        if (-not $match.Success) { throw "The $Kind path cannot be mapped authoritatively to a local volume: $Path" }
        $Collection.Add($match.Groups['drive'].Value.ToUpperInvariant() + ':\')
    }
    $systemDrive = [string]$env:SystemDrive
    foreach ($pageFile in @(Get-CimInstance -ClassName Win32_PageFileUsage -ErrorAction Stop)) {
        Add-ProtectedPathRoot $pageFileRoots ([string]$pageFile.Name) 'pagefile'
    }
    foreach ($pageFile in @(Get-CimInstance -ClassName Win32_PageFileSetting -ErrorAction Stop)) {
        Add-ProtectedPathRoot $pageFileRoots ([string]$pageFile.Name) 'configured pagefile'
    }
    $crash = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' -ErrorAction Stop
    foreach ($dumpPath in @([string]$crash.DumpFile, [string]$crash.MinidumpDir)) {
        Add-ProtectedPathRoot $crashDumpRoots $dumpPath 'crash-dump'
    }
    $protectedIds = @($volumes | Where-Object {
        $root = [string]$_.DriveLetter + ':\'
        $root -ieq $systemDrive.TrimEnd('\') + '\' -or $pageFileRoots.Contains($root) -or $crashDumpRoots.Contains($root)
    } | ForEach-Object { [string]$_.UniqueId })
    $isProtected = $protectedIds -contains [string]$volume.UniqueId

    $targetPartitionFlags = @($partitions | Where-Object { [string]$_.DriveLetter -ieq $driveLetter })
    $partitionRecovery = @($targetPartitionFlags | Where-Object { [string]$_.Type -match 'Recovery' }).Count -gt 0
    $devicePathCanonical = '\\.\' + $driveLetter + ':'
    return [pscustomobject]@{
        IsWindows = $true
        IsPhysicalMachine = [bool]$physical
        OperatingSystem = [string]$os.Caption
        OsBuild = [string]$os.BuildNumber
        CpuName = [string]$processor.Name
        CpuLogicalCount = [int]$computer.NumberOfLogicalProcessors
        MemoryMiB = [int64]([double]$computer.TotalPhysicalMemory / 1MB)
        SystemDrive = $systemDrive
        DevicePath = $devicePathCanonical
        MarkerPath = $markerFullPath
        Seed = [pscustomobject]@{
            Path = $vhdxPath
            Attached = [bool]$vhd.Attached
            VhdFormat = [string]$vhd.VhdFormat
            VhdType = [string]$vhd.VhdType
            SizeBytes = [int64]$vhd.Size
            DiskNumber = [int]$vhd.DiskNumber
            RunId = [string]$marker.RunId
        }
        Disk = [pscustomobject]@{
            Number = [int]$disk.Number
            UniqueId = [string]$disk.UniqueId
            BusType = [string]$disk.BusType
            OperationalStatus = [string]$disk.OperationalStatus
            IsSystem = [bool]$disk.IsSystem
            IsBoot = [bool]$disk.IsBoot
        }
        Volume = [pscustomobject]@{
            UniqueId = [string]$volume.UniqueId
            VolumeGuidPath = $volumePath
            DriveLetter = $driveLetter
            DriveRoot = $driveLetter + ':\'
            FileSystem = [string]$volume.FileSystem
            FileSystemLabel = [string]$volume.FileSystemLabel
            IsSystem = @($targetPartitionFlags | Where-Object { [bool]$_.IsSystem -or [string]$_.Type -match 'System' }).Count -gt 0
            IsBoot = @($targetPartitionFlags | Where-Object { [bool]$_.IsBoot }).Count -gt 0
            IsRecovery = $partitionRecovery
            IsPageFile = $pageFileRoots.Contains($driveLetter + ':\')
            IsCrashDump = $crashDumpRoots.Contains($driveLetter + ':\')
        }
        Marker = $marker
        ProtectedVolumeUniqueIds = $protectedIds
    }
}
