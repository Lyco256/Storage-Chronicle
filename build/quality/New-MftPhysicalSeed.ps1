[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)][string]$ApprovedRoot,
    [Parameter(Mandatory = $true)][string]$EvidenceParent,
    [Parameter(Mandatory = $true)][guid]$RunId,
    [ValidateRange(64, 256)][int]$VhdxSizeGiB = 96,
    [switch]$Apply,
    [string]$ConfirmationToken
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'MftSeedWorkflow.Contracts.ps1')
. (Join-Path $PSScriptRoot 'MftPhysicalSeed.Contracts.ps1')

try {
    $plan = Get-MftSeedWorkflowPlan -ApprovedRoot $ApprovedRoot -EvidenceParent $EvidenceParent -RunId $RunId -VhdxSizeGiB $VhdxSizeGiB
    $plan | Format-List | Out-String | Write-Host
    if (-not $Apply) {
        Write-Output 'DRY_RUN_ONLY: no directories, files, VHDX, disk, partition, volume, or workload data were changed.'
        Write-Output (ConvertTo-Json -InputObject $plan -Depth 8)
        exit 0
    }

    $expectedToken = 'CREATE MFT SEED ' + $RunId.ToString('D')
    if (-not [string]::Equals($ConfirmationToken, $expectedToken, [StringComparison]::Ordinal)) {
        throw "Apply requires -ConfirmationToken '$expectedToken' after reviewing the displayed target, VHDX size, format target, and one-million-file workload."
    }
    if (-not (Test-MftWorkflowAdministrator)) { throw 'Apply requires an explicitly elevated PowerShell session. This script never self-elevates.' }
    if (-not $PSCmdlet.ShouldProcess("$($plan.VhdxPath); $($plan.VolumeLabel); $($plan.DatasetEntryCount) metadata-only files", 'Create, attach, initialize, partition, format, and seed a new dynamic VHDX')) {
        Write-Output 'WHATIF_OR_DECLINED: no storage changes were made.'
        exit 0
    }

    $dotnetExecutable = (Get-Command 'dotnet.exe' -CommandType Application -ErrorAction Stop).Source
    if ([string]::IsNullOrWhiteSpace($dotnetExecutable) -or -not (Test-Path -LiteralPath $dotnetExecutable -PathType Leaf)) { throw 'The .NET host executable could not be resolved before seed creation.' }
    $dotnetExecutable = [IO.Path]::GetFullPath($dotnetExecutable)

    Assert-MftWorkflowPlanStorageRoots -Plan $plan
    $evidenceRun = New-MftSeedEvidenceRun -Plan $plan
    $intent = [ordered]@{
        Schema = 'StorageChronicle.MftSeedRunIntent.v1'
        RunId = $RunId.ToString('D')
        CreatedUtc = [DateTimeOffset]::UtcNow
        ApprovedRoot = $plan.ApprovedRoot
        EvidenceRoot = $plan.EvidenceRoot
        VhdxPath = $plan.VhdxPath
        VhdxSizeGiB = $VhdxSizeGiB
        VhdxType = 'Dynamic'
        VolumeLabel = $plan.VolumeLabel
        DatasetEntryCount = $plan.DatasetEntryCount
        Workload = 'StorageChronicle.FileMutationWorkload --scenario mft --count 1000000'
        HostInventoryBefore = $plan.HostInventory
        ProvenanceLimit = 'Creator-authored intent is not independent write-monitor evidence and cannot alone make a benchmark acceptance eligible.'
    }
    Assert-MftWorkflowPlanStorageRoots -Plan $plan
    Write-MftCreateNewJson -Path $plan.IntentPath -Value $intent
    $intentHash = Get-MftSha256 -LiteralPath $plan.IntentPath

    $rootVolume = Get-Volume -DriveLetter $plan.RootDriveLetter -ErrorAction Stop
    Assert-MftWorkflowHostVolume -Volume $rootVolume -Path $plan.ApprovedRoot
    if ([int64]$rootVolume.SizeRemaining -lt 60GB) { throw 'The host volume has less than 60 GiB free immediately before seed creation.' }
    Assert-MftWorkflowPlanStorageRoots -Plan $plan
    New-MftDirectoryCreateNew -Path ([string]$plan.SeedDirectory) | Out-Null
    if (Test-Path -LiteralPath $plan.VhdxPath) { throw 'The GUID VHDX path appeared after preflight; refusing to overwrite.' }

    Assert-MftWorkflowPlanStorageRoots -Plan $plan
    if (Test-Path -LiteralPath $plan.VhdxPath) { throw 'The GUID VHDX path appeared immediately before create; refusing to overwrite.' }
    $vhdxCreateAction = { param($path, $sizeBytes) New-VHD -Path $path -Dynamic -SizeBytes $sizeBytes -ErrorAction Stop | Out-Null }
    New-MftVhdxCreateNew -Path $plan.VhdxPath -SizeBytes ([int64]$VhdxSizeGiB * 1GB) -CreateAction $vhdxCreateAction | Out-Null
    $vhdBeforeAttach = Get-VHD -Path $plan.VhdxPath -ErrorAction Stop
    if ([string]$vhdBeforeAttach.VhdFormat -ne 'VHDX' -or [string]$vhdBeforeAttach.VhdType -ne 'Dynamic' -or [int64]$vhdBeforeAttach.Size -ne ([int64]$VhdxSizeGiB * 1GB)) { throw 'New-VHD returned unexpected format, type, or virtual size.' }
    $vhdFileIdentity = Get-MftWindowsFileIdentity -Path $plan.VhdxPath
    $preAttachVhd = Get-VHD -Path $plan.VhdxPath -ErrorAction Stop
    if ([IO.Path]::GetFullPath([string]$preAttachVhd.Path) -ne [IO.Path]::GetFullPath($plan.VhdxPath) -or [bool]$preAttachVhd.Attached -or [string]$preAttachVhd.VhdType -ne 'Dynamic' -or (Get-MftWindowsFileIdentity -Path $plan.VhdxPath) -ne $vhdFileIdentity) { throw 'The new VHDX path/identity changed before attach.' }
    Mount-VHD -Path $plan.VhdxPath -NoDriveLetter -ErrorAction Stop | Out-Null
    $vhd = Get-VHD -Path $plan.VhdxPath -ErrorAction Stop
    $disk = Get-Disk -Number ([int]$vhd.DiskNumber) -ErrorAction Stop
    Assert-MftWorkflowNewRawDisk -Disk $disk -VhdPath $plan.VhdxPath -Vhd $vhd
    if (@(Get-Partition -DiskNumber ([int]$disk.Number) -ErrorAction Stop).Count -ne 0) { throw 'The attached new VHDX unexpectedly contains partitions; refusing initialization/format.' }

    $preInitializeVhd = Get-VHD -Path $plan.VhdxPath -ErrorAction Stop
    $preInitializeDisk = Get-Disk -Number ([int]$preInitializeVhd.DiskNumber) -ErrorAction Stop
    Assert-MftWorkflowNewRawDisk -Disk $preInitializeDisk -VhdPath $plan.VhdxPath -Vhd $preInitializeVhd
    if (@(Get-Partition -DiskNumber ([int]$preInitializeDisk.Number) -ErrorAction Stop).Count -ne 0) { throw 'The new disk acquired a partition immediately before initialization.' }
    Initialize-Disk -UniqueId ([string]$preInitializeDisk.UniqueId) -PartitionStyle GPT -PassThru -ErrorAction Stop | Out-Null
    $disk = Get-Disk -Number ([int]$disk.Number) -ErrorAction Stop
    Assert-MftWorkflowNewRawDisk -Disk $disk -VhdPath $plan.VhdxPath -Vhd (Get-VHD -Path $plan.VhdxPath -ErrorAction Stop) -AllowInitialized
    $partitions = @(Get-Partition -DiskNumber ([int]$disk.Number) -ErrorAction Stop)
    if ($partitions.Count -ne 0) { throw 'A partition appeared after initialization before the workflow created its sole partition.' }
    $driveLetter = Get-MftWorkflowAvailableDriveLetter
    $prePartitionVhd = Get-VHD -Path $plan.VhdxPath -ErrorAction Stop
    $prePartitionDisk = Get-Disk -Number ([int]$prePartitionVhd.DiskNumber) -ErrorAction Stop
    Assert-MftWorkflowNewRawDisk -Disk $prePartitionDisk -VhdPath $plan.VhdxPath -Vhd $prePartitionVhd -AllowInitialized
    if ([string]$prePartitionDisk.UniqueId -ne [string]$disk.UniqueId -or @(Get-Partition -DiskNumber ([int]$prePartitionDisk.Number) -ErrorAction Stop).Count -ne 0) { throw 'VHDX identity or empty partition table changed immediately before partition creation.' }
    $partition = New-Partition -DiskNumber ([int]$disk.Number) -UseMaximumSize -DriveLetter $driveLetter -ErrorAction Stop
    $disk = Get-Disk -Number ([int]$disk.Number) -ErrorAction Stop
    $partitions = @(Get-Partition -DiskNumber ([int]$disk.Number) -ErrorAction Stop)
    if ($partitions.Count -ne 1 -or [int]$partitions[0].PartitionNumber -ne [int]$partition.PartitionNumber -or [string]$partitions[0].Type -match 'Recovery|System|Reserved' -or [string]$disk.UniqueId -ne [string]$vhd.DiskIdentifier) { throw 'Disk/partition identity changed before format; refusing.' }
    $volume = Get-Volume -Partition $partition -ErrorAction Stop
    if ([string]$volume.FileSystem -and [string]$volume.FileSystem -ne 'RAW') { throw 'The new partition is not RAW; refusing to format.' }
    if ([string]$volume.UniqueId -in @(Get-MftSeedWorkflowHostInventory).ProtectedVolumeUniqueIds -or [string]$driveLetter -ieq ([string]$env:SystemDrive).TrimEnd(':')) { throw 'The format target resolves to a protected host volume or system drive.' }
    $formatVhd = Get-VHD -Path $plan.VhdxPath -ErrorAction Stop
    $formatDisk = Get-Disk -Number ([int]$formatVhd.DiskNumber) -ErrorAction Stop
    Assert-MftWorkflowNewRawDisk -Disk $formatDisk -VhdPath $plan.VhdxPath -Vhd $formatVhd -AllowInitialized
    $formatPartitions = @(Get-Partition -DiskNumber ([int]$formatDisk.Number) -ErrorAction Stop)
    if ([IO.Path]::GetFullPath([string]$formatVhd.Path) -ne [IO.Path]::GetFullPath($plan.VhdxPath) -or [string]$formatDisk.UniqueId -ne [string]$disk.UniqueId -or $formatPartitions.Count -ne 1 -or [int]$formatPartitions[0].PartitionNumber -ne [int]$partition.PartitionNumber -or [string]$formatPartitions[0].Type -match 'Recovery|System|Reserved|EFI' -or ([string]$formatPartitions[0].DriveLetter) -ine $driveLetter) { throw 'Disk/partition/path identity changed immediately before format; refusing.' }
    Format-Volume -Partition $partition -FileSystem NTFS -NewFileSystemLabel $plan.VolumeLabel -Confirm:$false -Force -ErrorAction Stop | Out-Null
    $volume = Get-Volume -Partition $partition -ErrorAction Stop
    $volumeGuid = [string]$volume.Path
    if ([string]$volume.FileSystem -ne 'NTFS' -or [string]$volume.FileSystemLabel -ne $plan.VolumeLabel -or $volumeGuid -notmatch '^\\\?\\Volume\{[0-9a-fA-F-]+\}\\$') { throw 'Formatted volume identity/label did not match the new seed contract.' }

    $workloadRoot = Join-Path ($driveLetter + ':\') ('StorageChronicle-Mft-' + $RunId.ToString('N'))
    $null = Assert-MftWorkflowMountedSeedIdentity -VhdxPath $plan.VhdxPath -VhdxFileIdentity $vhdFileIdentity -DiskUniqueId ([string]$disk.UniqueId) -DriveLetter $driveLetter -VolumeUniqueId ([string]$volume.UniqueId) -VolumeLabel $plan.VolumeLabel
    New-MftDirectoryCreateNew -Path $workloadRoot | Out-Null
    $volumeUniqueId = [string]$volume.UniqueId
    $testMarker = [ordered]@{ Schema = 'StorageChronicle.TestLabDataMarker.v1'; TestId = $RunId.ToString('D'); Role = 'Mft'; VolumeLabel = $plan.VolumeLabel; FileSystem = 'NTFS'; VolumeUniqueId = $volumeUniqueId; CreatedUtc = [DateTimeOffset]::UtcNow }
    $null = Assert-MftWorkflowMountedSeedIdentity -VhdxPath $plan.VhdxPath -VhdxFileIdentity $vhdFileIdentity -DiskUniqueId ([string]$disk.UniqueId) -DriveLetter $driveLetter -VolumeUniqueId $volumeUniqueId -VolumeLabel $plan.VolumeLabel
    Write-MftCreateNewJson -Path (Join-Path $workloadRoot '.storage-chronicle-testlab-marker.json') -Value $testMarker
    $null = Assert-MftWorkflowMountedSeedIdentity -VhdxPath $plan.VhdxPath -VhdxFileIdentity $vhdFileIdentity -DiskUniqueId ([string]$disk.UniqueId) -DriveLetter $driveLetter -VolumeUniqueId $volumeUniqueId -VolumeLabel $plan.VolumeLabel
    Write-MftCreateNewJson -Path (Join-Path $workloadRoot 'StorageChronicleTestVolume.json') -Value $testMarker
    $workloadProject = Join-Path $plan.RepositoryRoot 'tools/StorageChronicle.FileMutationWorkload/StorageChronicle.FileMutationWorkload.csproj'
    $workloadOutput = Join-Path $evidenceRun 'workload-publish'
    $isolatedObj = Join-Path $evidenceRun 'obj'
    $isolatedTemp = Join-Path $evidenceRun 'temp'
    $isolatedNuget = Join-Path $evidenceRun 'nuget'
    $isolatedCli = Join-Path $evidenceRun 'dotnet-cli'
    foreach ($directory in @($isolatedObj, $isolatedTemp, $isolatedNuget, $isolatedCli, $workloadOutput)) { New-MftDirectoryCreateNew -Path $directory | Out-Null }
    $savedEnvironment = @{}; foreach ($name in @('TEMP','TMP','NUGET_PACKAGES','DOTNET_CLI_HOME')) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    try {
        $env:TEMP = $isolatedTemp; $env:TMP = $isolatedTemp; $env:NUGET_PACKAGES = $isolatedNuget; $env:DOTNET_CLI_HOME = $isolatedCli
        $publishArgs = @('publish', $workloadProject, '-c', 'Release', '-o', $workloadOutput, '--nologo', '-p:BaseIntermediateOutputPath=' + $isolatedObj + '\', '-p:MSBuildProjectExtensionsPath=' + $isolatedObj + '\')
        $publish = Start-Process -FilePath $dotnetExecutable -ArgumentList (ConvertTo-MftProcessArgumentLine -Argument $publishArgs) -WorkingDirectory $plan.RepositoryRoot -Wait -PassThru -NoNewWindow
    }
    finally { foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, [string]$savedEnvironment[$name], 'Process') } }
    if ($publish.ExitCode -ne 0) { throw "FileMutationWorkload publish failed with exit code $($publish.ExitCode); evidence and VHDX are retained." }
    $dotnetSha256 = Get-MftSha256 -LiteralPath $dotnetExecutable
    $publishProcess = [ordered]@{ ProcessId = $publish.Id; StartTimeUtc = $publish.StartTime.ToUniversalTime().ToString('o'); ExecutablePath = $dotnetExecutable; Sha256 = $dotnetSha256 }
    $workloadDll = Join-Path $workloadOutput 'StorageChronicle.FileMutationWorkload.dll'
    if (-not (Test-Path -LiteralPath $workloadDll -PathType Leaf)) { throw 'The published MFT workload executable is missing.' }
    $oraclePath = Join-Path $workloadRoot ('oracle-' + $RunId.ToString('D') + '.json')
    Assert-MftWorkflowPlanStorageRoots -Plan $plan
    $null = Assert-MftWorkflowMountedSeedIdentity -VhdxPath $plan.VhdxPath -VhdxFileIdentity $vhdFileIdentity -DiskUniqueId ([string]$disk.UniqueId) -DriveLetter $driveLetter -VolumeUniqueId $volumeUniqueId -VolumeLabel $plan.VolumeLabel
    $rootVolume = Get-Volume -DriveLetter $plan.RootDriveLetter -ErrorAction Stop
    if ([int64]$rootVolume.SizeRemaining -lt 60GB) { throw 'The host volume has less than 60 GiB free immediately before the one-million-file workload.' }
    $workloadArgs = @($workloadDll, '--root', $workloadRoot, '--oracle', $oraclePath, '--scenario', 'mft', '--count', '1000000', '--run-id', $RunId.ToString('D'))
    $workload = Start-Process -FilePath $dotnetExecutable -ArgumentList (ConvertTo-MftProcessArgumentLine -Argument $workloadArgs) -Wait -PassThru -NoNewWindow
    if ($workload.ExitCode -ne 0) { throw "MFT workload failed with exit code $($workload.ExitCode); VHDX, fixture, and evidence are retained." }
    $workloadProcess = [ordered]@{ ProcessId = $workload.Id; StartTimeUtc = $workload.StartTime.ToUniversalTime().ToString('o'); ExecutablePath = $dotnetExecutable; Sha256 = $dotnetSha256; WorkloadSha256 = (Get-MftSha256 -LiteralPath $workloadDll) }
    $oracle = Get-Content -LiteralPath $oraclePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$oracle.RunId -ne $RunId.ToString('D') -or [string]$oracle.Scenario -ne 'mft' -or [int64]$oracle.RecordCount -ne 1000000) { throw 'The workload oracle does not prove one million entries for this run.' }
    $actualEntries = @(Get-ChildItem -LiteralPath $workloadRoot -Force -File -Recurse -ErrorAction Stop).Count
    if ($actualEntries -lt 1000003) { throw "The seed contains only $actualEntries files; expected one million dataset files plus two markers and an oracle." }

    $vhd = Get-VHD -Path $plan.VhdxPath -ErrorAction Stop
    $disk = Get-Disk -Number ([int]$vhd.DiskNumber) -ErrorAction Stop
    $volume = Get-Volume -DriveLetter $driveLetter -ErrorAction Stop
    $finalFileIdentity = Get-MftWindowsFileIdentity -Path $plan.VhdxPath
    if ($finalFileIdentity -ne $vhdFileIdentity -or [string]$disk.UniqueId -ne [string]$vhd.DiskIdentifier -or [string]$volume.UniqueId -ne $volumeUniqueId) { throw 'Final VHDX/disk/volume identity changed; retaining all evidence and fixture.' }
    $seedMarkerPath = Join-Path $workloadRoot 'MftSeed.json'
    $seedMarker = [ordered]@{
        Schema = 'StorageChronicle.MftSeedMarker.v2'; Role = 'MftSeed'; RunId = $RunId.ToString('D'); VhdxPath = $plan.VhdxPath; VhdxFileIdentity = $vhdFileIdentity
        DiskNumber = [int]$disk.Number; DiskUniqueId = [string]$disk.UniqueId; VolumeUniqueId = $volumeUniqueId; VolumeGuidPath = $volumeGuid
        VolumeLabel = $plan.VolumeLabel; FileSystem = 'NTFS'; DatasetEntryCount = 1000000; WorkloadRoot = $workloadRoot; IntentPath = $plan.IntentPath; IntentSha256 = $intentHash
    }
    $null = Assert-MftWorkflowMountedSeedIdentity -VhdxPath $plan.VhdxPath -VhdxFileIdentity $vhdFileIdentity -DiskUniqueId ([string]$disk.UniqueId) -DriveLetter $driveLetter -VolumeUniqueId $volumeUniqueId -VolumeLabel $plan.VolumeLabel
    Write-MftCreateNewJson -Path $seedMarkerPath -Value $seedMarker
    $completion = [ordered]@{
        Schema = 'StorageChronicle.MftSeedCreationEvidence.v1'; Status = 'SEEDED'; RunId = $RunId.ToString('D'); IntentPath = $plan.IntentPath; IntentSha256 = $intentHash
        ApprovedRoot = $plan.ApprovedRoot; EvidenceRoot = $plan.EvidenceRoot; VhdxPath = $plan.VhdxPath; VhdxFileIdentity = $finalFileIdentity; VhdxSizeGiB = $VhdxSizeGiB; VhdxType = 'Dynamic'
        DiskNumber = [int]$disk.Number; DiskUniqueId = [string]$disk.UniqueId; VolumeUniqueId = [string]$volume.UniqueId; VolumeGuidPath = $volumeGuid; VolumeLabel = $plan.VolumeLabel
        SeedMarkerPath = $seedMarkerPath; WorkloadRoot = $workloadRoot; WorkloadOraclePath = $oraclePath; DatasetEntryCount = 1000000; ObservedWorkloadFileCount = $actualEntries + 1
        Processes = @(
            [ordered]@{ Role = 'SeedCreator'; ProcessId = $PID; StartTimeUtc = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o'); ExecutablePath = (Get-Process -Id $PID).Path; Sha256 = (Get-MftSha256 -LiteralPath (Get-Process -Id $PID).Path) }
            [ordered]@{ Role = 'WorkloadPublisher'; ProcessId = $publishProcess.ProcessId; StartTimeUtc = $publishProcess.StartTimeUtc; ExecutablePath = $publishProcess.ExecutablePath; Sha256 = $publishProcess.Sha256 }
            [ordered]@{ Role = 'MftWorkload'; ProcessId = $workloadProcess.ProcessId; StartTimeUtc = $workloadProcess.StartTimeUtc; ExecutablePath = $workloadProcess.ExecutablePath; Sha256 = $workloadProcess.Sha256; WorkloadSha256 = $workloadProcess.WorkloadSha256 }
        )
        IndependentWriteMonitor = [ordered]@{ Required = $true; Status = 'NOT_SUPPLIED'; EvidencePath = $null; AcceptanceEligible = $false }
        HostInventoryBefore = $plan.HostInventory; HostInventoryAfter = Get-MftWorkflowHostInventory
        CompletionUtc = [DateTimeOffset]::UtcNow
    }
    Assert-MftWorkflowPlanStorageRoots -Plan $plan
    Write-MftCreateNewJson -Path $plan.CreationEvidencePath -Value $completion
    Write-Output "Seed created and workload completed. No rollback or cleanup was attempted. Independent write-monitor evidence is still required; physical benchmark acceptance remains blocked. Evidence: $($plan.CreationEvidencePath)"
}
catch {
    Write-Error ("MFT seed workflow stopped fail-closed; no automatic delete/rollback was attempted. Existing partial evidence/fixture is retained. " + $_.Exception.Message)
    exit 1
}
