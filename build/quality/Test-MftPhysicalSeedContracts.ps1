[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $PSScriptRoot 'MftPhysicalSeed.Contracts.ps1')
. (Join-Path $PSScriptRoot 'MftSeedWorkflow.Contracts.ps1')

$runId = '8d9b1f4c-2976-4e19-9fe0-0caa99b24f01'
$seedPath = Join-Path (Join-Path ([IO.Path]::GetTempPath()) $runId) 'MFT-seed.vhdx'
$inventory = [pscustomobject]@{
    IsWindows = $true; IsPhysicalMachine = $true; OperatingSystem = 'Microsoft Windows 11 Pro'; OsBuild = '26100'
    CpuName = 'Contract CPU'; CpuLogicalCount = 8; MemoryMiB = 16384; SystemDrive = 'C:'
    DevicePath = '\\.\D:'; MarkerPath = 'D:\.StorageChronicle\MftSeed.json'; ProtectedVolumeUniqueIds = @()
    Seed = [pscustomobject]@{ Path = $seedPath; Attached = $true; VhdFormat = 'VHDX'; VhdType = 'Dynamic'; SizeBytes = 64GB; DiskNumber = 12; RunId = $runId }
    Disk = [pscustomobject]@{ Number = 12; UniqueId = 'disk-id-contract'; BusType = 'File Backed Virtual'; OperationalStatus = 'Online'; IsSystem = $false; IsBoot = $false }
    Volume = [pscustomobject]@{ UniqueId = 'volume-id-contract'; VolumeGuidPath = '\\?\Volume{3f8c9012-4552-4e2d-a5b9-0caa99b24f01}\'; DriveLetter = 'D'; DriveRoot = 'D:\'; FileSystem = 'NTFS'; FileSystemLabel = 'SC_TEST_MFT_VOLUME'; IsSystem = $false; IsBoot = $false; IsRecovery = $false; IsPageFile = $false; IsCrashDump = $false }
    Marker = [pscustomobject]@{ Schema = 'StorageChronicle.MftSeedMarker.v1'; Role = 'MftSeed'; RunId = $runId; VhdxPath = $seedPath; DiskUniqueId = 'disk-id-contract'; VolumeUniqueId = 'volume-id-contract'; VolumeGuidPath = '\\?\Volume{3f8c9012-4552-4e2d-a5b9-0caa99b24f01}\'; VolumeLabel = 'SC_TEST_MFT_VOLUME'; FileSystem = 'NTFS'; DatasetEntryCount = 1000000 }
}

$passed = 0
$failed = [System.Collections.Generic.List[string]]::new()
function Invoke-ContractCase([string]$Name, [scriptblock]$Body) {
    try { & $Body; $script:passed++; Write-Host "PASS $Name" }
    catch { $script:failed.Add("$Name`: $($_.Exception.Message)"); Write-Host "FAIL $Name" -ForegroundColor Red }
}
function Assert-Refused([string]$Name, [scriptblock]$Mutation) {
    Invoke-ContractCase $Name {
        $case = $script:inventory | ConvertTo-Json -Depth 8 | ConvertFrom-Json
        & $Mutation $case
        try { Assert-MftPhysicalSeedInventory $case | Out-Null; throw 'Unsafe MFT seed inventory was accepted.' }
        catch { if ($_.Exception.Message -eq 'Unsafe MFT seed inventory was accepted.') { throw } }
    }
}

Invoke-ContractCase 'valid physical dynamic VHDX identities pass' {
    $result = Assert-MftPhysicalSeedInventory $inventory
    if ($result.DatasetEntryCount -ne 1000000 -or $result.DiskUniqueId -ne 'disk-id-contract') { throw 'Verified identity was not retained in evidence.' }
}
Invoke-ContractCase 'v2 marker file identity and dataset fields are verified' {
    $case = $inventory | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $case.Marker = [pscustomobject]@{
        Schema = 'StorageChronicle.MftSeedMarker.v2'; Role = 'MftSeed'; RunId = $runId; VhdxPath = $seedPath; VhdxFileIdentity = 'vhd-file-id-contract'
        DiskNumber = 12; DiskUniqueId = 'disk-id-contract'; VolumeUniqueId = 'volume-id-contract'; VolumeGuidPath = '\\?\Volume{3f8c9012-4552-4e2d-a5b9-0caa99b24f01}\'
        VolumeLabel = 'SC_TEST_MFT_VOLUME'; FileSystem = 'NTFS'; DatasetEntryCount = 1000000; WorkloadRoot = 'D:\StorageChronicle-Mft-Contract'
        IntentPath = 'C:\evidence\run-intent.json'; IntentSha256 = ('a' * 64)
    }
    function Get-MftWindowsFileIdentity([string]$Path) { 'vhd-file-id-contract' }
    $result = Assert-MftPhysicalSeedInventory $case
    if ($result.DatasetEntryCount -ne 1000000) { throw 'v2 marker did not retain the verified dataset count.' }
}
Invoke-ContractCase 'full MFT runner requires external monitor and creation provenance before any suite starts' {
    $runner = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build/quality/Test-FullBenchmarkMatrix.ps1') -Raw
    if ($runner.Contains('Windows 11 TestLab guest') -or $runner.Contains('STORAGE_CHRONICLE_MFT_VM_CPU_COUNT') -or $runner.Contains('STORAGE_CHRONICLE_MFT_VM_MEMORY_MIB')) { throw 'The full matrix still depends on guest/VM evidence.' }
    foreach ($required in @('MftCreationEvidencePath', 'MftWriteMonitorEvidencePath', 'Assert-MftSeedCreationEvidence', 'Assert-MftIndependentWriteMonitorEvidence', 'Write-NotExecuted', 'Assert-MftExternalArtifactRoot')) {
        if (-not $runner.Contains($required)) { throw "The matrix is missing fail-closed provenance gate $required." }
    }
    if ($runner.IndexOf('Assert-MftIndependentWriteMonitorEvidence', [StringComparison]::Ordinal) -gt $runner.IndexOf('$suites = @(', [StringComparison]::Ordinal)) { throw 'Independent write-monitor evidence is checked only after benchmark execution begins.' }
    if (-not $runner.Contains('MFT acceptance requires an explicit existing ArtifactRoot') -or -not $runner.Contains('Assert-MftExternalArtifactRoot')) { throw 'MFT artifacts may still default into the repository or a synchronized path.' }
    $finalGate = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build/quality/Test-FinalAcceptance.ps1') -Raw
    foreach ($field in @('DiskNumber', 'DiskUniqueId', 'VolumeUniqueId', 'VolumeGuidPath', 'DevicePath', 'MarkerPath', 'SeedRunId', 'DatasetEntryCount', 'CpuLogicalCount', 'MemoryMiB', 'VhdxSizeGiB')) {
        if (-not $finalGate.Contains($field)) { throw "Final acceptance does not require physical seed field $field." }
    }
}
Invoke-ContractCase 'seed workflow has a mutation-free WhatIf path and explicit typed approval' {
    $workflow = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build/quality/New-MftPhysicalSeed.ps1') -Raw
    $contracts = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build/quality/MftSeedWorkflow.Contracts.ps1') -Raw
    if (-not $workflow.Contains('SupportsShouldProcess = $true') -or -not $workflow.Contains('if (-not $Apply)') -or -not $workflow.Contains('ShouldProcess(') -or -not $workflow.Contains('ConfirmationToken')) { throw 'The workflow lacks WhatIf, Apply, ShouldProcess, or exact typed approval.' }
    if ($workflow.IndexOf('if (-not $Apply)', [StringComparison]::Ordinal) -gt $workflow.IndexOf('New-VHD ', [StringComparison]::Ordinal)) { throw 'Dry-run guard is not before VHDX creation.' }
    foreach ($required in @('60 GiB', 'Fixed', 'NTFS', 'ReparsePoint', 'SC_TEST_MFT_VOLUME', 'ProtectedVolumeUniqueIds', 'CreateNew', 'File Backed Virtual', 'ProtectedVolumeRoots', 'DumpFile', 'MinidumpDir', 'Assert-MftWorkflowOutsideRepository', 'Initialize-Disk -UniqueId')) {
        if (-not ($workflow + $contracts).Contains($required)) { throw "Seed workflow is missing required safety control: $required." }
    }
    foreach ($required in @('CreateDirectoryW', 'New-MftDirectoryCreateNew', 'New-MftVhdxCreateNew', 'CreateDirectoryAction', 'CreateAction')) {
        if (-not $contracts.Contains($required)) { throw "Workflow does not provide atomic/injectable create-new contract $required." }
    }
    if ($workflow.Contains('[IO.Directory]::CreateDirectory')) { throw 'Run-owned roots must not use idempotent Directory.CreateDirectory.' }
    if ($workflow -match '(?im)^\s*(Remove-VHD|Remove-Item|Clear-Disk|Dismount-VHD)\b') { throw 'Failure handling must never auto-delete, rollback, or detach evidence/fixture.' }
}
Invoke-ContractCase 'atomic directory and VHDX create seams fail closed on a concurrent collision' {
    $contracts = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build/quality/MftSeedWorkflow.Contracts.ps1') -Raw
    $collisionPath = Join-Path ([IO.Path]::GetTempPath()) ('sc-mft-contract-' + [guid]::NewGuid().ToString('N'))
    $directoryCollisionObserved = $false
    try { New-MftDirectoryCreateNew -Path $collisionPath -CreateDirectoryAction { param($path) throw [IO.IOException]::new('simulated ERROR_ALREADY_EXISTS') } | Out-Null }
    catch { $directoryCollisionObserved = $_.Exception.Message -match 'simulated ERROR_ALREADY_EXISTS' }
    if (-not $directoryCollisionObserved) { throw 'Atomic CreateDirectoryW seam did not propagate an AlreadyExists collision.' }

    $vhdxCollisionObserved = $false
    try { New-MftVhdxCreateNew -Path $collisionPath -SizeBytes 96GB -CreateAction { param($path, $bytes) throw [IO.IOException]::new('simulated New-VHD CREATE_NEW collision') } | Out-Null }
    catch { $vhdxCollisionObserved = $_.Exception.Message -match 'simulated New-VHD CREATE_NEW collision' }
    if (-not $vhdxCollisionObserved) { throw 'VHDX create seam did not propagate a concurrent no-replace collision.' }

    $existingPath = Join-Path $repositoryRoot 'build/quality/Test-MftPhysicalSeedContracts.ps1'
    $createInvoked = $false
    try { New-MftVhdxCreateNew -Path $existingPath -SizeBytes 96GB -CreateAction { $createInvoked = $true; return $true } | Out-Null; throw 'Existing VHDX target was accepted.' }
    catch { if ($_.Exception.Message -eq 'Existing VHDX target was accepted.') { throw } }
    if ($createInvoked) { throw 'VHDX create action was invoked for an existing target.' }
}
Invoke-ContractCase 'Windows argument quoting preserves whitespace and trailing backslashes offline' {
    $quotedPath = ConvertTo-MftWindowsCommandLineArgument -Argument 'C:\isolated build\obj\'
    $quotedProperty = ConvertTo-MftWindowsCommandLineArgument -Argument '-p:BaseIntermediateOutputPath=C:\isolated build\obj\'
    $quotedEmbeddedQuote = ConvertTo-MftWindowsCommandLineArgument -Argument 'prefix"suffix'
    if ($quotedPath -cne '"C:\isolated build\obj\\"') { throw "Path argument was not safely quoted: $quotedPath" }
    if ($quotedProperty -cne '"-p:BaseIntermediateOutputPath=C:\isolated build\obj\\"') { throw "MSBuild property was not safely quoted: $quotedProperty" }
    if ($quotedEmbeddedQuote -cne '"prefix\"suffix"') { throw "Embedded quote was not safely escaped: $quotedEmbeddedQuote" }
    $line = ConvertTo-MftProcessArgumentLine -Argument @('C:\isolated build\obj\', '-p:MSBuildProjectExtensionsPath=C:\isolated build\obj\')
    if ($line -cne '"C:\isolated build\obj\\" "-p:MSBuildProjectExtensionsPath=C:\isolated build\obj\\"') { throw "Combined Windows command-line quoting changed arguments: $line" }
}
Invoke-ContractCase 'MFT benchmark binds provenance fields and writes correctness evidence create-new under external root' {
    $benchmark = Get-Content -LiteralPath (Join-Path $repositoryRoot 'benchmarks/StorageChronicle.Benchmarks/MftBenchmarks.cs') -Raw
    $runner = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build/quality/Test-FullBenchmarkMatrix.ps1') -Raw
    foreach ($field in @('SeedVhdxFileIdentity', 'SeedWorkloadRoot', 'SeedWorkloadOraclePath', 'SeedCreationEvidencePath', 'SeedCreationEvidenceSha256', 'SeedWriteMonitorEvidencePath', 'SeedWriteMonitorEvidenceSha256', 'SeedProcessIdentities')) {
        if (-not $benchmark.Contains($field)) { throw "MFT benchmark evidence omits final-gate provenance field $field." }
    }
    foreach ($guard in @('STORAGE_CHRONICLE_MFT_ARTIFACT_ROOT', 'Path.GetRelativePath', 'The MFT evidence parent must already exist', 'FileMode.CreateNew', 'ValidatePhysicalSeedPreflight', 'TraceFinalized', 'LostEventCount', 'DroppedEventCount')) {
        if (-not $benchmark.Contains($guard)) { throw "MFT correctness output is missing safe provenance/output guard $guard." }
    }
    if ($benchmark.Contains('Directory.CreateDirectory') -or $benchmark.Contains('File.WriteAllText')) { throw 'MFT correctness evidence must not create arbitrary parents or overwrite existing evidence.' }
    foreach ($envName in @('STORAGE_CHRONICLE_MFT_SEED_VHDX_FILE_IDENTITY', 'STORAGE_CHRONICLE_MFT_SEED_WORKLOAD_ROOT', 'STORAGE_CHRONICLE_MFT_SEED_WORKLOAD_ORACLE_PATH', 'STORAGE_CHRONICLE_MFT_CREATION_EVIDENCE_SHA256', 'STORAGE_CHRONICLE_MFT_WRITE_MONITOR_EVIDENCE_SHA256', 'STORAGE_CHRONICLE_MFT_SEED_PROCESS_IDENTITIES')) {
        if (-not $runner.Contains($envName)) { throw "Full matrix does not pass verified provenance to the MFT benchmark: $envName." }
    }
}
Invoke-ContractCase 'seed records bind create-new intent, file identity, disk/volume IDs, and exact dataset count' {
    $workflow = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build/quality/New-MftPhysicalSeed.ps1') -Raw
    foreach ($required in @('MftSeedRunIntent.v1', 'CreateNew', 'MftSeedMarker.v2', 'VhdxFileIdentity', 'DiskUniqueId', 'VolumeUniqueId', 'IntentSha256', 'DatasetEntryCount = 1000000', '--scenario', 'mft', '--count', '1000000', 'ObservedWorkloadFileCount')) {
        if (-not $workflow.Contains($required)) { throw "Create-new seed provenance is missing $required." }
    }
    if (-not $workflow.Contains("Status = 'NOT_SUPPLIED'") -or -not $workflow.Contains('AcceptanceEligible = $false')) { throw 'Creator-owned records must not claim independent monitor acceptance.' }
    if (-not $workflow.Contains("Get-Command 'dotnet.exe' -CommandType Application") -or $workflow.Contains('$publish.Path') -or $workflow.Contains('$workload.Path')) { throw 'Seed process evidence must resolve the executable before storage mutation and must not read a nonexistent Process.Path property.' }
    $dotnetResolutionIndex = $workflow.IndexOf('$dotnetExecutable = (Get-Command', [StringComparison]::Ordinal)
    if ($dotnetResolutionIndex -gt $workflow.IndexOf('$evidenceRun = New-MftSeedEvidenceRun', [StringComparison]::Ordinal) -or
        $dotnetResolutionIndex -gt $workflow.IndexOf('$vhdxCreateAction =', [StringComparison]::Ordinal)) { throw 'The .NET host executable must resolve before any run evidence or VHDX creation can begin.' }
}
Invoke-ContractCase 'monitor validator checks independent source, read-only inventories, processes, paths, and trace digest' {
    $contracts = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build/quality/MftPhysicalSeed.Contracts.ps1') -Raw
    foreach ($required in @('MftSeedWriteMonitorEvidence.v1', 'IndependentCollector', 'ProcessAttributedFileWrites', 'TraceFinalized', 'CaptureComplete', 'LostEventCount', 'DroppedEventCount', 'EvidenceOutputPath', 'SourceTraceSha256', 'ReadOnlyInventoryCapturedBefore', 'ReadOnlyInventoryCapturedAfter', 'ProtectedInventoryUnchanged', 'StartTimeUtc', 'CaptureStartedUtc', 'CaptureCompletedUtc', 'ProcessId', 'outside creator-owned seed and evidence roots', 'WorkloadRoot', 'writeOperations', 'sawVhdx', 'sawWorkload')) {
        if (-not $contracts.Contains($required)) { throw "Independent monitor contract does not validate $required." }
    }
}
Invoke-ContractCase 'mocked seed-plan checks reject unsafe paths, roots, capacity, contents, filesystem, and protected roles' {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { return }
    $script:mockFreeBytes = [int64]100GB
    $script:mockRootHasContent = $false
    $script:mockReparsePath = ''
    $script:mockProtectedVolumeIds = @('system-volume', 'boot-volume', 'recovery-volume', 'pagefile-volume', 'crashdump-volume')
    $script:mockFileSystem = 'NTFS'
    $script:mockDriveType = 'Fixed'
    $script:mockRootPath = 'C:\MftContractRoot'
    $script:mockEvidencePath = 'D:\MftContractEvidence'
    function Get-Volume {
        param([string]$DriveLetter, $Partition)
        if ($Partition) { return [pscustomobject]@{ UniqueId = 'seed-volume'; FileSystem = 'RAW'; FileSystemLabel = ''; Path = 'D:\' } }
        $id = if ($DriveLetter -eq 'D') { 'evidence-volume' } else { 'root-volume' }
        [pscustomobject]@{ UniqueId = $id; FileSystem = $script:mockFileSystem; DriveType = $script:mockDriveType; IsReady = $true; SizeRemaining = $script:mockFreeBytes; DriveLetter = $DriveLetter }
    }
    function Test-Path {
        param([string]$LiteralPath, [string]$PathType)
        if ($LiteralPath -eq $script:mockReparsePath) { return $true }
        if ($LiteralPath -match '805f96c9-22d7-4c1f-b2cb-8e4194e4fd17|MftSeed-805f96c922d74c1fb2cb8e4194e4fd17') { return $false }
        return $true
    }
    function Get-Item {
        param([string]$LiteralPath, [switch]$Force, [string]$ErrorAction)
        $attributes = [IO.FileAttributes]::Directory
        if ($LiteralPath -eq $script:mockReparsePath) { $attributes = $attributes -bor [IO.FileAttributes]::ReparsePoint }
        [pscustomobject]@{ Attributes = $attributes; FullName = $LiteralPath }
    }
    function Get-ChildItem {
        param([string]$LiteralPath, [switch]$Force, [string]$ErrorAction)
        if ($LiteralPath -eq $script:mockRootPath -and $script:mockRootHasContent) { return ,@([pscustomobject]@{ Name = 'pre-existing' }) }
        return @()
    }
    function Get-MftSeedWorkflowHostInventory {
        [pscustomobject]@{ IsPhysicalMachine = $true; ProtectedVolumeUniqueIds = @($script:mockProtectedVolumeIds); ProtectedDiskUniqueIds = @('system-disk'); ComputerName = 'MOCKHOST'; OsBuild = 'mock' }
    }
    function Assert-MftWorkflowHostVolume {
        param($Volume, [string]$Path)
        if ($Volume.FileSystem -ne 'NTFS' -or $Volume.DriveType -ne 'Fixed') { throw 'mock filesystem/drive type refused' }
        Assert-MftWorkflowProtectedVolume -VolumeUniqueId $Volume.UniqueId -Inventory (Get-MftSeedWorkflowHostInventory)
    }
    function Assert-PlanRejected([string]$Name, [scriptblock]$Action) {
        try { & $Action; throw "Unsafe mocked plan accepted: $Name" }
        catch { if ($_.Exception.Message -eq "Unsafe mocked plan accepted: $Name") { throw } }
    }
    $planCall = { Get-MftSeedWorkflowPlan -ApprovedRoot $script:mockRootPath -EvidenceParent $script:mockEvidencePath -RunId ([guid]'805f96c9-22d7-4c1f-b2cb-8e4194e4fd17') -VhdxSizeGiB 96 }
    Assert-PlanRejected 'volume root' { Assert-MftWorkflowDirectory -Path 'C:\' -ParameterName 'ApprovedRoot' }
    Assert-PlanRejected 'inside repository' { Assert-MftWorkflowOutsideRepository -Path (Join-Path $script:repositoryRoot 'unsafe-root') }
    Assert-PlanRejected 'below OneDrive' {
        $saved = $env:OneDrive; $env:OneDrive = 'C:\Users\Mock\OneDrive'
        try { Assert-MftWorkflowDirectory -Path 'C:\Users\Mock\OneDrive\unsafe' -ParameterName 'ApprovedRoot' } finally { $env:OneDrive = $saved }
    }
    $script:mockDriveType = 'Removable'; Assert-PlanRejected 'non-fixed volume' { & $planCall }; $script:mockDriveType = 'Fixed'
    $script:mockFileSystem = 'exFAT'; Assert-PlanRejected 'non-NTFS volume' { & $planCall }; $script:mockFileSystem = 'NTFS'
    $script:mockFreeBytes = [int64]59GB; Assert-PlanRejected 'less than 60 GiB' { & $planCall }; $script:mockFreeBytes = [int64]100GB
    $script:mockRootHasContent = $true; Assert-PlanRejected 'existing root contents' { & $planCall }; $script:mockRootHasContent = $false
    $script:mockReparsePath = $script:mockRootPath; Assert-PlanRejected 'reparse root' { & $planCall }; $script:mockReparsePath = ''
    $script:mockProtectedVolumeIds += 'root-volume'; Assert-PlanRejected 'protected pagefile/recovery-class volume' { & $planCall }; $script:mockProtectedVolumeIds = @('system-volume','boot-volume','recovery-volume','pagefile-volume','crashdump-volume')
    Assert-PlanRejected 'evidence-volume identity mismatch' {
        $plan = & $planCall
        $plan.EvidenceVolumeUniqueId = 'stale-volume-id'
        Assert-MftWorkflowPlanStorageRoots -Plan $plan
    }
    $artifactRoot = Assert-MftExternalArtifactRoot -Path $script:mockRootPath
    if ($artifactRoot -ne $script:mockRootPath) { throw 'A valid mocked external fixed-NTFS ArtifactRoot was not accepted.' }
    Assert-PlanRejected 'Documents artifact root' { Assert-MftExternalArtifactRoot -Path (Join-Path $env:USERPROFILE 'Documents\SC-artifacts') }
}
Invoke-ContractCase 'independent monitor accepts run-bound VHDX and workload writes and rejects out-of-root or short capture' {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { return }
    $now = [DateTimeOffset]::UtcNow
    $intent = [pscustomobject]@{ CreatedUtc = $now.AddMinutes(-10).ToString('o') }
    $hostBefore = [pscustomobject]@{ ReadOnly = $true; ComputerName = 'MOCKHOST'; SystemVolumeUniqueId = 'system-volume'; ProtectedDiskUniqueIds = @('sysdisk'); ProtectedVolumeUniqueIds = @('system-volume','recovery-volume') }
    $hostAfter = $hostBefore | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    $processes = @(
        [pscustomobject]@{ Role = 'SeedCreator'; ProcessId = 410; StartTimeUtc = $now.AddMinutes(-9).ToString('o'); Sha256 = ('a' * 64) },
        [pscustomobject]@{ Role = 'WorkloadPublisher'; ProcessId = 411; StartTimeUtc = $now.AddMinutes(-7).ToString('o'); Sha256 = ('e' * 64) },
        [pscustomobject]@{ Role = 'MftWorkload'; ProcessId = 412; StartTimeUtc = $now.AddMinutes(-5).ToString('o'); Sha256 = ('b' * 64) }
    )
    $script:mockCreationEvidence = [pscustomobject]@{
        Schema = 'StorageChronicle.MftSeedCreationEvidence.v1'; Status = 'SEEDED'; DatasetEntryCount = 1000000; ObservedWorkloadFileCount = 1000004
        RunId = '805f96c9-22d7-4c1f-b2cb-8e4194e4fd17'; IntentPath = 'C:\seed-evidence\run-intent.json'; IntentSha256 = ('d' * 64); ApprovedRoot = 'C:\approved-root'; EvidenceRoot = 'C:\seed-evidence'
        WorkloadRoot = 'D:\StorageChronicle-Mft-run'; WorkloadOraclePath = 'D:\StorageChronicle-Mft-run\oracle-run.json'; VhdxPath = 'C:\approved-root\run\seed.vhdx'; VhdxFileIdentity = 'vhd-file-id'
        DiskNumber = 22; DiskUniqueId = 'seed-disk'; VolumeUniqueId = 'seed-volume'; VolumeGuidPath = '\\?\Volume{seed-guid}\'; VolumeLabel = 'SC_TEST_MFT_VOLUME'; VhdxType = 'Dynamic'; VhdxSizeGiB = 96
        SeedMarkerPath = 'D:\StorageChronicle-Mft-run\MftSeed.json'; CompletionUtc = $now.AddMinutes(-1).ToString('o')
        HostInventoryBefore = [pscustomobject]@{ ComputerName = 'MOCKHOST'; OsBuild = 'mock-build'; SystemVolumeUniqueId = 'system-volume' }; Processes = $processes
    }
    $script:mockMonitor = [pscustomobject]@{
        Schema = 'StorageChronicle.MftSeedWriteMonitorEvidence.v1'; Status = 'PASS'; RunId = $script:mockCreationEvidence.RunId
        Collection = [pscustomobject]@{ IndependentCollector = $true; CollectorName = 'ExternalMonitor'; Version = '1.0'; CaptureMode = 'ProcessAttributedFileWrites'; TraceFinalized = $true; CaptureComplete = $true; LostEventCount = 0; DroppedEventCount = 0 }
        EvidenceOutputPath = 'E:\monitor\evidence.json'; SourceTracePath = 'E:\monitor\trace.pml'; SourceTraceSha256 = ('c' * 64)
        CaptureStartedUtc = $now.AddMinutes(-11).ToString('o'); CaptureCompletedUtc = $now.ToString('o')
        Host = [pscustomobject]@{
            ComputerName = 'MOCKHOST'; OsBuild = 'mock-build'; ReadOnlyInventoryCapturedBefore = $true; ReadOnlyInventoryCapturedAfter = $true; ProtectedInventoryUnchanged = $true
            InventoryBefore = $hostBefore; InventoryAfter = $hostAfter; InventoryBeforeSha256 = (Get-MftObjectSha256 $hostBefore); InventoryAfterSha256 = (Get-MftObjectSha256 $hostAfter)
        }
        Target = [pscustomobject]@{ VhdxPath = $script:mockCreationEvidence.VhdxPath; DiskUniqueId = 'seed-disk'; VolumeUniqueId = 'seed-volume'; WorkloadRoot = $script:mockCreationEvidence.WorkloadRoot }
        Processes = @($processes + [pscustomobject]@{ Role = 'StorageHost'; ProcessId = 413; StartTimeUtc = $now.AddMinutes(-8).ToString('o'); Sha256 = ('d' * 64) })
        WriteEvents = @(
            [pscustomobject]@{ ProcessId = 410; Path = 'C:\approved-root\run\seed.vhdx'; Operation = 'Write'; Result = 'SUCCESS' },
            [pscustomobject]@{ ProcessId = 413; Path = 'D:\StorageChronicle-Mft-run\mft-0000\entry-00000000.dat'; Operation = 'Create'; Result = 'SUCCESS' }
        )
    }
    function Test-Path { param([string]$LiteralPath, [string]$PathType) return $true }
    function Get-Content { param([string]$LiteralPath, [switch]$Raw, [string]$Encoding) if ($LiteralPath -like '*run-intent.json') { return ($script:intent | ConvertTo-Json -Depth 4) }; if ($LiteralPath -like '*creation.json') { return ($script:mockCreationEvidence | ConvertTo-Json -Depth 14) }; return ($script:mockMonitor | ConvertTo-Json -Depth 14) }
    function Get-MftSha256 { param([string]$LiteralPath) if ($LiteralPath -like '*creation.json') { return ('a' * 64) }; if ($LiteralPath -like '*evidence.json') { return ('b' * 64) }; if ($LiteralPath -like '*run-intent.json') { return ('d' * 64) }; return ('c' * 64) }
    function Assert-MftWorkflowNoReparsePath { param([string]$Path) }
    $script:intent = $intent
    $check = { Assert-MftIndependentWriteMonitorEvidence -Path 'E:\monitor\evidence.json' -CreationEvidence $script:mockCreationEvidence -Inventory ([pscustomobject]@{}) }
    $result = & $check
    if ([string]$result.Status -ne 'PASS') { throw 'Valid external monitor evidence was not accepted.' }
    $badEvents = $script:mockMonitor.WriteEvents
    $script:mockMonitor.WriteEvents = @($badEvents[0], [pscustomobject]@{ ProcessId = 413; Path = 'C:\Users\Mock\Documents\other.txt'; Operation = 'Write'; Result = 'SUCCESS' })
    try { & $check; throw 'Out-of-root write was accepted.' } catch { if ($_.Exception.Message -eq 'Out-of-root write was accepted.') { throw } }
    $script:mockMonitor.WriteEvents = $badEvents
    $script:mockMonitor.CaptureStartedUtc = $now.AddMinutes(-2).ToString('o')
    try { & $check; throw 'Monitor capture beginning after run intent was accepted.' } catch { if ($_.Exception.Message -eq 'Monitor capture beginning after run intent was accepted.') { throw } }
    $script:mockMonitor.CaptureStartedUtc = $now.AddMinutes(-11).ToString('o')
    $script:mockMonitor.WriteEvents = @(
        [pscustomobject]@{ ProcessId = 410; Path = 'C:\approved-root\run\seed.vhdx'; Operation = 'Read'; Result = 'SUCCESS' },
        [pscustomobject]@{ ProcessId = 413; Path = 'D:\StorageChronicle-Mft-run\mft-0000\entry-00000000.dat'; Operation = 'Query'; Result = 'SUCCESS' }
    )
    try { & $check; throw 'Read/query events were accepted as write provenance.' } catch { if ($_.Exception.Message -eq 'Read/query events were accepted as write provenance.') { throw } }
    $script:mockMonitor.WriteEvents = $badEvents
    $script:mockMonitor.Collection.LostEventCount = 1
    try { & $check; throw 'Incomplete trace with lost events was accepted.' } catch { if ($_.Exception.Message -eq 'Incomplete trace with lost events was accepted.') { throw } }
    $script:mockMonitor.Collection.LostEventCount = 0
    $script:mockMonitor.Collection.TraceFinalized = $false
    try { & $check; throw 'Unfinalized trace was accepted.' } catch { if ($_.Exception.Message -eq 'Unfinalized trace was accepted.') { throw } }
    $script:mockMonitor.Collection.TraceFinalized = $true

    $matrixEnvironment = [pscustomobject]@{
        SeedRunId = $script:mockCreationEvidence.RunId; VhdxPath = $script:mockCreationEvidence.VhdxPath; SeedVhdxFileIdentity = $script:mockCreationEvidence.VhdxFileIdentity
        DiskNumber = $script:mockCreationEvidence.DiskNumber; DiskUniqueId = $script:mockCreationEvidence.DiskUniqueId; VolumeUniqueId = $script:mockCreationEvidence.VolumeUniqueId
        VolumeGuidPath = $script:mockCreationEvidence.VolumeGuidPath; MarkerPath = $script:mockCreationEvidence.SeedMarkerPath; SeedWorkloadRoot = $script:mockCreationEvidence.WorkloadRoot
        SeedWorkloadOraclePath = $script:mockCreationEvidence.WorkloadOraclePath; SeedCreationEvidencePath = 'C:\seed-evidence\creation.json'; SeedCreationEvidenceSha256 = ('a' * 64)
        SeedWriteMonitorEvidencePath = 'E:\monitor\evidence.json'; SeedWriteMonitorEvidenceSha256 = ('b' * 64); DatasetEntryCount = 1000000; VhdxType = 'Dynamic'; VhdxSizeGiB = 96
        SeedProcessIdentities = $processes
    }
    $matrix = [pscustomobject]@{
        AcceptanceEligible = $true; MftCreationEvidencePath = 'C:\seed-evidence\creation.json'; MftCreationEvidenceSha256 = ('a' * 64)
        MftWriteMonitorEvidencePath = 'E:\monitor\evidence.json'; MftWriteMonitorEvidenceSha256 = ('b' * 64)
        MftEvidence = [pscustomobject]@{ Environment = $matrixEnvironment }
    }
    $script:mockMonitor.WriteEvents = $badEvents
    $creationCheck = { Assert-MftFinalAcceptanceSeedProvenance -Matrix $script:matrix }
    $script:matrix = $matrix
    if (-not (& $creationCheck)) { throw 'Valid external creation and write-monitor evidence did not pass final aggregation.' }
    $savedMonitorPath = $matrix.MftWriteMonitorEvidencePath
    $matrix.MftWriteMonitorEvidencePath = ''
    try { & $creationCheck; throw 'Legacy/no-monitor matrix was accepted.' } catch { if ($_.Exception.Message -eq 'Legacy/no-monitor matrix was accepted.') { throw } }
    $matrix.MftWriteMonitorEvidencePath = $savedMonitorPath
    $matrix.MftCreationEvidenceSha256 = ('f' * 64)
    try { & $creationCheck; throw 'Tampered creator-evidence hash was accepted.' } catch { if ($_.Exception.Message -eq 'Tampered creator-evidence hash was accepted.') { throw } }
    $matrix.MftCreationEvidenceSha256 = ('a' * 64)
    $matrix.MftWriteMonitorEvidenceSha256 = ('f' * 64)
    try { & $creationCheck; throw 'Tampered independent-monitor hash was accepted.' } catch { if ($_.Exception.Message -eq 'Tampered independent-monitor hash was accepted.') { throw } }
    $matrix.MftWriteMonitorEvidenceSha256 = ('b' * 64)
    $matrix.AcceptanceEligible = $false
    try { & $creationCheck; throw 'Ineligible matrix was accepted by final provenance validation.' } catch { if ($_.Exception.Message -eq 'Ineligible matrix was accepted by final provenance validation.') { throw } }
    $matrix.AcceptanceEligible = $true
    $matrix.MftEvidence.Environment.DiskUniqueId = 'wrong-disk'
    try { & $creationCheck; throw 'Wrong benchmark target identity was accepted.' } catch { if ($_.Exception.Message -eq 'Wrong benchmark target identity was accepted.') { throw } }
}
Assert-Refused 'virtual machine host rejected' { param($x) $x.IsPhysicalMachine = $false }
Assert-Refused 'C drive MFT target rejected' { param($x) $x.DevicePath = '\\.\C:'; $x.Volume.DriveLetter = 'C' }
Assert-Refused 'unattached VHDX rejected' { param($x) $x.Seed.Attached = $false }
Assert-Refused 'fixed VHDX rejected' { param($x) $x.Seed.VhdType = 'Fixed' }
Assert-Refused 'disk identity mismatch rejected' { param($x) $x.Disk.UniqueId = 'different-disk' }
Assert-Refused 'VHDX attached to a different disk number rejected' { param($x) $x.Seed.DiskNumber = 13 }
Assert-Refused 'physical USB disk rejected' { param($x) $x.Disk.BusType = 'USB' }
Assert-Refused 'boot disk rejected' { param($x) $x.Disk.IsBoot = $true }
Assert-Refused 'volume identity mismatch rejected' { param($x) $x.Volume.VolumeGuidPath = '\\?\Volume{different}\' }
Assert-Refused 'volume unique ID mismatch rejected' { param($x) $x.Volume.UniqueId = 'different-volume' }
Assert-Refused 'device path and volume letter mismatch rejected' { param($x) $x.DevicePath = '\\.\E:' }
Assert-Refused 'system volume rejected' { param($x) $x.Volume.IsSystem = $true }
Assert-Refused 'pagefile volume rejected' { param($x) $x.ProtectedVolumeUniqueIds = @('volume-id-contract') }
Assert-Refused 'crash dump volume rejected' { param($x) $x.Volume.IsCrashDump = $true }
Assert-Refused 'recovery volume rejected' { param($x) $x.Volume.IsRecovery = $true }
Assert-Refused 'wrong volume label rejected' { param($x) $x.Volume.FileSystemLabel = 'DATA' }
Assert-Refused 'VHDX path mismatch with persistent marker rejected' { param($x) $x.Marker.VhdxPath = 'C:\other\seed.vhdx' }
Assert-Refused 'marker must remain on seed volume' { param($x) $x.MarkerPath = 'C:\MftSeed.json' }
Assert-Refused 'stale marker run GUID rejected' { param($x) $x.Marker.RunId = 'not-a-guid' }
Assert-Refused 'undersized MFT dataset rejected' { param($x) $x.Marker.DatasetEntryCount = 999999 }

Write-Output "MftPhysicalSeedContracts Passed=$passed Failed=$($failed.Count)"
if ($failed.Count -gt 0) { $failed | ForEach-Object { Write-Output "CONTRACT_FAILURE $_" }; exit 1 }
