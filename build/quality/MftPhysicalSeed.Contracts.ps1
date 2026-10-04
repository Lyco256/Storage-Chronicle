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
    if ([string]$marker.Schema -notin @('StorageChronicle.MftSeedMarker.v1', 'StorageChronicle.MftSeedMarker.v2') -or
        [string]$marker.Role -ne 'MftSeed' -or
        [string]$marker.FileSystem -ne 'NTFS' -or
        [string]$marker.VolumeLabel -ne 'SC_TEST_MFT_VOLUME') { throw 'The persistent MFT seed marker schema or role is invalid.' }
    Require-Equal 'VHDX path' ([IO.Path]::GetFullPath([string]$seed.Path)) ([IO.Path]::GetFullPath([string]$marker.VhdxPath))
    if ([IO.Path]::GetFileName([IO.Path]::GetDirectoryName([string]$seed.Path)) -ne [string]$marker.RunId) { throw 'The VHDX must be in its run-GUID-named isolation directory.' }
    if ([string]::IsNullOrWhiteSpace([string]$Inventory.MarkerPath) -or
        -not [string]::Equals([IO.Path]::GetPathRoot([IO.Path]::GetFullPath([string]$Inventory.MarkerPath)), [string]$volume.DriveRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'The marker is not on the verified MFT seed volume.' }
    if ([int64]$marker.DatasetEntryCount -lt 1000000) { throw 'The persistent seed marker does not declare at least one million dataset entries.' }
    if ([string]$marker.Schema -eq 'StorageChronicle.MftSeedMarker.v2') {
        if ([string]::IsNullOrWhiteSpace([string]$marker.VhdxFileIdentity) -or
            [int]$marker.DiskNumber -ne [int]$disk.Number -or
            [string]$marker.WorkloadRoot -notmatch ('^' + [regex]::Escape([string]$volume.DriveLetter) + ':\\') -or
            [string]::IsNullOrWhiteSpace([string]$marker.IntentPath) -or
            [string]$marker.IntentSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'The v2 seed marker is missing its file, disk, workload-root, or intent identity.' }
        if ((Get-MftWindowsFileIdentity -Path ([string]$seed.Path)) -ne [string]$marker.VhdxFileIdentity) { throw 'The VHDX file identity no longer matches the persistent seed marker.' }
    }
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
        VhdxFileIdentity = if ($null -ne $marker.PSObject.Properties['VhdxFileIdentity']) { [string]$marker.VhdxFileIdentity } else { '' }
        VhdxType = [string]$seed.VhdType
        VhdxSizeGiB = [math]::Round([double]$seed.SizeBytes / 1GB, 2)
        DiskNumber = [int]$disk.Number
        DiskUniqueId = [string]$disk.UniqueId
        VolumeUniqueId = [string]$volume.UniqueId
        VolumeGuidPath = [string]$volume.VolumeGuidPath
        VolumeLabel = [string]$volume.FileSystemLabel
        DevicePath = [string]$Inventory.DevicePath
        MarkerPath = [IO.Path]::GetFullPath([string]$Inventory.MarkerPath)
        WorkloadRoot = if ($null -ne $marker.PSObject.Properties['WorkloadRoot']) { [string]$marker.WorkloadRoot } else { '' }
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

function Assert-MftSeedCreationEvidence {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)]$Inventory)
    $full = [IO.Path]::GetFullPath($Path)
    Assert-MftWorkflowNoReparsePath -Path $full
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw 'Run-scoped MFT seed creation evidence is missing.' }
    $evidence = Get-Content -LiteralPath $full -Raw -Encoding UTF8 | ConvertFrom-Json
    $marker = $Inventory.Marker
    if ([string]$evidence.Schema -ne 'StorageChronicle.MftSeedCreationEvidence.v1' -or [string]$evidence.Status -ne 'SEEDED' -or [string]$evidence.RunId -ne [string]$marker.RunId) { throw 'Seed creation evidence schema, status, or run GUID is invalid.' }
    foreach ($pair in @(
        @([string]$evidence.VhdxPath, [string]$marker.VhdxPath),
        @([string]$evidence.VhdxFileIdentity, [string]$marker.VhdxFileIdentity),
        @([string]$evidence.DiskUniqueId, [string]$marker.DiskUniqueId),
        @([string]$evidence.VolumeUniqueId, [string]$marker.VolumeUniqueId),
        @([string]$evidence.VolumeGuidPath, [string]$marker.VolumeGuidPath),
        @([string]$evidence.SeedMarkerPath, [string]$Inventory.MarkerPath),
        @([string]$evidence.WorkloadRoot, [string]$marker.WorkloadRoot))) {
        if (-not [string]::Equals($pair[0], $pair[1], [StringComparison]::OrdinalIgnoreCase)) { throw 'Seed creation evidence does not match the live disk/volume/marker identity.' }
    }
    if ([int]$evidence.DiskNumber -ne [int]$Inventory.Disk.Number -or [int64]$evidence.DatasetEntryCount -ne 1000000 -or [int64]$evidence.ObservedWorkloadFileCount -lt 1000003) { throw 'Seed creation evidence does not prove the exact run disk and one-million-file workload.' }
    if ([string]$marker.Schema -ne 'StorageChronicle.MftSeedMarker.v2' -or [string]$evidence.IntentPath -ne [string]$marker.IntentPath -or [string]$evidence.IntentSha256 -ne [string]$marker.IntentSha256) { throw 'The seed marker and creation evidence are not bound to the same create-new intent.' }
    if (-not (Test-Path -LiteralPath ([string]$evidence.IntentPath) -PathType Leaf) -or (Get-MftSha256 -LiteralPath ([string]$evidence.IntentPath)) -ne [string]$evidence.IntentSha256) { throw 'The pre-create run intent is absent or its hash does not match.' }
    if ([string]$evidence.IndependentWriteMonitor.Status -ne 'NOT_SUPPLIED' -or [bool]$evidence.IndependentWriteMonitor.AcceptanceEligible) { throw 'Creator-authored records cannot attest independent write-monitor acceptance.' }
    return $evidence
}

function Assert-MftIndependentWriteMonitorEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$CreationEvidence,
        [Parameter(Mandatory = $true)]$Inventory
    )
    $full = [IO.Path]::GetFullPath($Path)
    Assert-MftWorkflowNoReparsePath -Path $full
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw 'A separately collected process-attributed write-monitor evidence file is required.' }
    $evidence = Get-Content -LiteralPath $full -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$evidence.Schema -ne 'StorageChronicle.MftSeedWriteMonitorEvidence.v1' -or [string]$evidence.Status -ne 'PASS') { throw 'External write-monitor evidence schema/status is not accepted.' }
    if (-not [bool]$evidence.Collection.IndependentCollector -or [string]::IsNullOrWhiteSpace([string]$evidence.Collection.CollectorName) -or [string]::IsNullOrWhiteSpace([string]$evidence.Collection.Version) -or [string]$evidence.Collection.CaptureMode -ne 'ProcessAttributedFileWrites') { throw 'Write-monitor evidence does not identify an independent process-attributed file-write collector.' }
    if (-not [bool]$evidence.Collection.TraceFinalized -or -not [bool]$evidence.Collection.CaptureComplete -or [int64]$evidence.Collection.LostEventCount -ne 0 -or [int64]$evidence.Collection.DroppedEventCount -ne 0) { throw 'External write-monitor trace is incomplete, unfinalized, or reports lost/dropped events.' }
    if (-not [string]::Equals([IO.Path]::GetFullPath([string]$evidence.EvidenceOutputPath), $full, [StringComparison]::OrdinalIgnoreCase) -or [string]::IsNullOrWhiteSpace([string]$evidence.SourceTracePath) -or [string]$evidence.SourceTraceSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'The external monitor output path or source-trace digest is invalid.' }
    if (-not (Test-Path -LiteralPath ([string]$evidence.SourceTracePath) -PathType Leaf) -or (Get-MftSha256 -LiteralPath ([string]$evidence.SourceTracePath)) -ne [string]$evidence.SourceTraceSha256) { throw 'The external monitor source trace is missing or its SHA-256 digest differs.' }
    $creatorRoots = [System.Collections.Generic.List[string]]::new()
    $creatorRoots.Add([IO.Path]::GetFullPath([string]$CreationEvidence.ApprovedRoot).TrimEnd('\') + '\')
    $creatorRoots.Add([IO.Path]::GetFullPath([string]$CreationEvidence.EvidenceRoot).TrimEnd('\') + '\')
    $creatorRoots.Add([IO.Path]::GetFullPath([string]$CreationEvidence.WorkloadRoot).TrimEnd('\') + '\')
    foreach ($output in @($full, [IO.Path]::GetFullPath([string]$evidence.SourceTracePath))) {
        if (@($creatorRoots | Where-Object { $output.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) { throw 'Independent monitor output/trace must be outside creator-owned seed and evidence roots.' }
    }
    if ([string]$evidence.RunId -ne [string]$CreationEvidence.RunId -or [string]$evidence.Host.ComputerName -ne [string]$CreationEvidence.HostInventoryBefore.ComputerName -or [string]$evidence.Host.OsBuild -ne [string]$CreationEvidence.HostInventoryBefore.OsBuild) { throw 'External monitor run or host identity does not match the seed creation record.' }
    $monitorStart = [DateTimeOffset]::Parse([string]$evidence.CaptureStartedUtc).ToUniversalTime()
    $monitorEnd = [DateTimeOffset]::Parse([string]$evidence.CaptureCompletedUtc).ToUniversalTime()
    $intentRecord = Get-Content -LiteralPath ([string]$CreationEvidence.IntentPath) -Raw -Encoding UTF8 | ConvertFrom-Json
    $intentStart = [DateTimeOffset]::Parse([string]$intentRecord.CreatedUtc).ToUniversalTime()
    $seedCompletion = [DateTimeOffset]::Parse([string]$CreationEvidence.CompletionUtc).ToUniversalTime()
    if ($monitorStart -gt $intentStart -or $monitorEnd -lt $seedCompletion -or $monitorEnd -le $monitorStart) { throw 'Independent monitor capture timestamps do not cover the full seed creation and completion interval.' }
    if (-not [bool]$evidence.Host.ReadOnlyInventoryCapturedBefore -or -not [bool]$evidence.Host.ReadOnlyInventoryCapturedAfter -or -not [bool]$evidence.Host.ProtectedInventoryUnchanged -or [string]::IsNullOrWhiteSpace([string]$evidence.Host.InventoryBeforeSha256) -or [string]::IsNullOrWhiteSpace([string]$evidence.Host.InventoryAfterSha256)) { throw 'External monitor evidence lacks the required before/after read-only host inventory.' }
    foreach ($inventory in @($evidence.Host.InventoryBefore, $evidence.Host.InventoryAfter)) {
        if (-not [bool]$inventory.ReadOnly -or [string]$inventory.ComputerName -ne [string]$evidence.Host.ComputerName -or [string]::IsNullOrWhiteSpace([string]$inventory.SystemVolumeUniqueId) -or @($inventory.ProtectedDiskUniqueIds).Count -eq 0 -or @($inventory.ProtectedVolumeUniqueIds).Count -eq 0) { throw 'The monitor host inventory is not read-only or lacks authoritative protected identities.' }
    }
    if ([string]$evidence.Host.InventoryBefore.SystemVolumeUniqueId -ne [string]$evidence.Host.InventoryAfter.SystemVolumeUniqueId -or [string]$evidence.Host.InventoryBefore.SystemVolumeUniqueId -ne [string]$CreationEvidence.HostInventoryBefore.SystemVolumeUniqueId -or (@($evidence.Host.InventoryBefore.ProtectedDiskUniqueIds | Sort-Object) -join '|') -ne (@($evidence.Host.InventoryAfter.ProtectedDiskUniqueIds | Sort-Object) -join '|') -or (@($evidence.Host.InventoryBefore.ProtectedVolumeUniqueIds | Sort-Object) -join '|') -ne (@($evidence.Host.InventoryAfter.ProtectedVolumeUniqueIds | Sort-Object) -join '|')) { throw 'Protected read-only host inventory changed across the seed operation.' }
    if ((Get-MftObjectSha256 $evidence.Host.InventoryBefore) -ne [string]$evidence.Host.InventoryBeforeSha256 -or (Get-MftObjectSha256 $evidence.Host.InventoryAfter) -ne [string]$evidence.Host.InventoryAfterSha256) { throw 'Read-only host inventory canonical content hashes do not match.' }
    foreach ($pair in @(
        @([string]$evidence.Target.VhdxPath, [string]$CreationEvidence.VhdxPath),
        @([string]$evidence.Target.DiskUniqueId, [string]$CreationEvidence.DiskUniqueId),
        @([string]$evidence.Target.VolumeUniqueId, [string]$CreationEvidence.VolumeUniqueId),
        @([string]$evidence.Target.WorkloadRoot, [string]$CreationEvidence.WorkloadRoot))) {
        if (-not [string]::Equals($pair[0], $pair[1], [StringComparison]::OrdinalIgnoreCase)) { throw 'External monitor target identities do not match the seeded VHDX and volume.' }
    }
    $observedProcesses = @($evidence.Processes)
    foreach ($expectedProcess in $CreationEvidence.Processes) {
        $expectedStart = [DateTimeOffset]::Parse([string]$expectedProcess.StartTimeUtc).ToUnixTimeSeconds()
        $matches = @($observedProcesses | Where-Object { [int]$_.ProcessId -eq [int]$expectedProcess.ProcessId -and [DateTimeOffset]::Parse([string]$_.StartTimeUtc).ToUnixTimeSeconds() -eq $expectedStart -and [string]$_.Sha256 -eq [string]$expectedProcess.Sha256 })
        if ($matches.Count -ne 1) { throw "External monitor evidence does not identify exactly one matching seed process: $($expectedProcess.Role); expected PID=$($expectedProcess.ProcessId), observed=$(@($observedProcesses | ForEach-Object { '{0}/{1}/{2}' -f $_.ProcessId, $_.StartTimeUtc, $_.Sha256 }) -join ';')." }
    }
    $events = @($evidence.WriteEvents)
    if ($events.Count -eq 0) { throw 'External monitor evidence contains no process-attributed write events.' }
    $writeOperations = @('Create', 'Write', 'Append', 'Rename', 'SetMetadata', 'SetLength', 'Delete', 'Format', 'Initialize', 'Partition', 'Attach', 'Detach')
    $allowedRoots = [System.Collections.Generic.List[string]]::new()
    $allowedRoots.Add([IO.Path]::GetFullPath([string]$CreationEvidence.ApprovedRoot).TrimEnd('\') + '\')
    $allowedRoots.Add([IO.Path]::GetFullPath([string]$CreationEvidence.EvidenceRoot).TrimEnd('\') + '\')
    $allowedRoots.Add([IO.Path]::GetFullPath([string]$CreationEvidence.WorkloadRoot).TrimEnd('\') + '\')
    $sawVhdx = $false
    $sawWorkload = $false
    foreach ($event in $events) {
        if (@($observedProcesses | Where-Object { [int]$_.ProcessId -eq [int]$event.ProcessId }).Count -ne 1 -or [string]$event.Operation -notin $writeOperations -or [string]::IsNullOrWhiteSpace([string]$event.Path) -or [string]$event.Result -ne 'SUCCESS') { throw 'External monitor contains a non-write, un-attributed, incomplete, or failed event.' }
        $eventPath = [IO.Path]::GetFullPath([string]$event.Path)
        $withinApproved = @($allowedRoots | Where-Object { $eventPath.StartsWith([string]$_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
        if (-not $withinApproved) { throw "A monitored process wrote outside the approved seed/evidence roots: $eventPath (allowed roots: $($allowedRoots -join ', '))." }
        if ([string]::Equals($eventPath, [string]$CreationEvidence.VhdxPath, [StringComparison]::OrdinalIgnoreCase)) { $sawVhdx = $true }
        if ($eventPath.StartsWith([IO.Path]::GetFullPath([string]$CreationEvidence.WorkloadRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { $sawWorkload = $true }
    }
    if (-not $sawVhdx -or -not $sawWorkload) { throw 'External monitor does not show writes to both this run VHDX and its new workload root.' }
    return $evidence
}

function Get-MftObjectSha256 {
    param([Parameter(Mandatory = $true)]$Value)
    $json = ConvertTo-Json -InputObject $Value -Depth 16 -Compress
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [Convert]::ToHexString($sha.ComputeHash($bytes)).ToLowerInvariant() } finally { $sha.Dispose() }
}

function Assert-MftFinalAcceptanceSeedProvenance {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Matrix)
    if (-not [bool]$Matrix.AcceptanceEligible) { throw 'Full benchmark matrix is not AcceptanceEligible=true.' }
    foreach ($field in @('MftCreationEvidencePath', 'MftCreationEvidenceSha256', 'MftWriteMonitorEvidencePath', 'MftWriteMonitorEvidenceSha256')) {
        if ([string]::IsNullOrWhiteSpace([string]$Matrix.$field)) { throw "Full benchmark matrix is missing provenance field $field." }
    }
    $creationPath = [IO.Path]::GetFullPath([string]$Matrix.MftCreationEvidencePath)
    $monitorPath = [IO.Path]::GetFullPath([string]$Matrix.MftWriteMonitorEvidencePath)
    Assert-MftWorkflowNoReparsePath -Path $creationPath
    Assert-MftWorkflowNoReparsePath -Path $monitorPath
    if (-not (Test-Path -LiteralPath $creationPath -PathType Leaf) -or -not (Test-Path -LiteralPath $monitorPath -PathType Leaf)) { throw 'Seed creation or independent write-monitor evidence file is missing.' }
    if ((Get-MftSha256 -LiteralPath $creationPath) -ne [string]$Matrix.MftCreationEvidenceSha256 -or (Get-MftSha256 -LiteralPath $monitorPath) -ne [string]$Matrix.MftWriteMonitorEvidenceSha256) { throw 'Creation or external monitor evidence hash differs from the full-matrix manifest.' }
    $creation = Get-Content -LiteralPath $creationPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$creation.Schema -ne 'StorageChronicle.MftSeedCreationEvidence.v1' -or [string]$creation.Status -ne 'SEEDED' -or [int64]$creation.DatasetEntryCount -ne 1000000) { throw 'Final acceptance requires successful create-new seed evidence for exactly one million dataset files.' }
    $environment = $Matrix.MftEvidence.Environment
    foreach ($pair in @(
        @([string]$environment.SeedRunId, [string]$creation.RunId),
        @([string]$environment.VhdxPath, [string]$creation.VhdxPath),
        @([string]$environment.SeedVhdxFileIdentity, [string]$creation.VhdxFileIdentity),
        @([string]$environment.DiskUniqueId, [string]$creation.DiskUniqueId),
        @([string]$environment.VolumeUniqueId, [string]$creation.VolumeUniqueId),
        @([string]$environment.VolumeGuidPath, [string]$creation.VolumeGuidPath),
        @([string]$environment.MarkerPath, [string]$creation.SeedMarkerPath),
        @([string]$environment.SeedWorkloadRoot, [string]$creation.WorkloadRoot),
        @([string]$environment.SeedWorkloadOraclePath, [string]$creation.WorkloadOraclePath),
        @([string]$environment.SeedCreationEvidencePath, $creationPath),
        @([string]$environment.SeedCreationEvidenceSha256, [string]$Matrix.MftCreationEvidenceSha256),
        @([string]$environment.SeedWriteMonitorEvidencePath, $monitorPath),
        @([string]$environment.SeedWriteMonitorEvidenceSha256, [string]$Matrix.MftWriteMonitorEvidenceSha256))) {
        if (-not [string]::Equals($pair[0], $pair[1], [StringComparison]::OrdinalIgnoreCase)) { throw 'Full matrix MFT environment does not match seed creation path/identity/provenance.' }
    }
    if ([int]$environment.DiskNumber -ne [int]$creation.DiskNumber -or [int64]$environment.DatasetEntryCount -ne [int64]$creation.DatasetEntryCount -or [string]$environment.VhdxType -ne [string]$creation.VhdxType -or [double]$environment.VhdxSizeGiB -ne [double]$creation.VhdxSizeGiB -or [string]$creation.VolumeLabel -ne 'SC_TEST_MFT_VOLUME') { throw 'Full matrix MFT environment size/count/disk/volume facts disagree with seed creation evidence.' }
    $seedProcesses = @($creation.Processes)
    $environmentProcesses = @($environment.SeedProcessIdentities)
    if ($seedProcesses.Count -lt 3 -or $environmentProcesses.Count -ne $seedProcesses.Count) { throw 'Full matrix environment does not preserve all seed creator/publisher/workload process identities.' }
    foreach ($process in $seedProcesses) {
        $expectedStart = [DateTimeOffset]::Parse([string]$process.StartTimeUtc).ToUnixTimeSeconds()
        $matches = @($environmentProcesses | Where-Object { [string]$_.Role -eq [string]$process.Role -and [int]$_.ProcessId -eq [int]$process.ProcessId -and [DateTimeOffset]::Parse([string]$_.StartTimeUtc).ToUnixTimeSeconds() -eq $expectedStart -and [string]$_.Sha256 -eq [string]$process.Sha256 })
        if ($matches.Count -ne 1) { throw "Full matrix environment does not bind process identity $($process.Role)." }
    }
    if (-not (Test-Path -LiteralPath ([string]$creation.IntentPath) -PathType Leaf) -or (Get-MftSha256 -LiteralPath ([string]$creation.IntentPath)) -ne [string]$creation.IntentSha256) { throw 'Pre-create intent is missing or does not match its creation evidence hash.' }
    $monitor = Assert-MftIndependentWriteMonitorEvidence -Path $monitorPath -CreationEvidence $creation -Inventory ([pscustomobject]@{})
    if ([string]$monitor.EvidenceOutputPath -ne $monitorPath -or [string]$monitor.RunId -ne [string]$creation.RunId) { throw 'External monitor evidence output path/run ID does not match final matrix provenance.' }
    return $true
}
