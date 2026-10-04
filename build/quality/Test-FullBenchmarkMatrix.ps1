[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$NoRestore,
    [switch]$IncludeMft,
    [switch]$PortableOnly,
    [string]$ArtifactRoot,
    [string]$MftEvidencePath,
    [string]$MftCreationEvidencePath,
    [string]$MftWriteMonitorEvidencePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $root 'benchmarks/StorageChronicle.Benchmarks/StorageChronicle.Benchmarks.csproj'
. (Join-Path $PSScriptRoot 'MftPhysicalSeed.Contracts.ps1')
. (Join-Path $PSScriptRoot 'MftSeedWorkflow.Contracts.ps1')

if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    if ($IncludeMft) {
        Write-Error 'MFT acceptance requires an explicit existing ArtifactRoot outside the repository, OneDrive/Documents, and protected host volumes; no run artifact was created.'
        exit 2
    }
    $ArtifactRoot = Join-Path $root 'artifacts/benchmarks'
}
if ($IncludeMft) {
    try { $ArtifactRoot = Assert-MftExternalArtifactRoot -Path $ArtifactRoot }
    catch { Write-Error "MFT ArtifactRoot validation failed before creating run evidence: $($_.Exception.Message)"; exit 2 }
}
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ', [Globalization.CultureInfo]::InvariantCulture)
$runStartedUtc = [DateTimeOffset]::UtcNow
$runRoot = Join-Path $ArtifactRoot ("full-matrix-" + $stamp)
New-MftDirectoryCreateNew -Path $runRoot | Out-Null
$windowsProductName = ''
if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
    $operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction SilentlyContinue
    if ($null -ne $operatingSystem) { $windowsProductName = [string]$operatingSystem.Caption }
}

if ([string]::IsNullOrWhiteSpace($MftEvidencePath)) {
    $MftEvidencePath = Join-Path (Join-Path $runRoot 'WindowsMft') 'mft-evidence.json'
}
$oldMftEvidencePath = $null
$mftPhysicalPreflightPath = $null
if ([string]::IsNullOrWhiteSpace($MftCreationEvidencePath)) { $MftCreationEvidencePath = [Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_MFT_CREATION_EVIDENCE_PATH') }
if ([string]::IsNullOrWhiteSpace($MftWriteMonitorEvidencePath)) { $MftWriteMonitorEvidencePath = [Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_MFT_WRITE_MONITOR_EVIDENCE_PATH') }

function Assert-MftEvidence {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The MFT correctness evidence was not produced: $Path"
    }

    $evidence = Get-Content -Raw -Encoding UTF8 -LiteralPath $Path | ConvertFrom-Json
    if ([string]$evidence.Schema -ne 'StorageChronicle.MftBenchmarkEvidence.v1') {
        throw "The MFT evidence schema is not supported: $Path"
    }
    if ([string]$evidence.Status -ne 'PASSED') {
        throw "The MFT evidence status is not PASSED: $($evidence.Status)"
    }
    if (-not [bool]$evidence.AcceptanceEligible) {
        throw "The MFT evidence is present but not acceptance-eligible: $Path"
    }

    $requiredMethods = @(
        'MftEnumerationImport10K',
        'MftEnumerationImport100K',
        'MftEnumerationImport1M',
        'MftCandidateMetadataQueriesZero1M',
        'MftCandidateMetadataQueriesSmall1M')
    $runs = @($evidence.Runs)
    foreach ($method in $requiredMethods) {
        $matching = @($runs | Where-Object { [string]$_.Method -eq $method })
        if ($matching.Count -ne 1) { throw "MFT evidence must contain exactly one run for $method; found $($matching.Count)." }
        $run = $matching[0]
        foreach ($field in @('DatasetEntryCount', 'EnumeratedEntryCount', 'CandidateCount', 'DetailedMetadataQueryCount', 'GeneratedCanonicalCount', 'DroppedEventCount', 'ElapsedMilliseconds', 'AllocatedBytes')) {
            $property = $run.PSObject.Properties[$field]
            if ($null -eq $property -or $null -eq $property.Value) { throw "MFT run $method is missing $field." }
            if ([double]$property.Value -lt 0) { throw "MFT run $method contains a negative $field." }
        }
        if ([int64]$run.DroppedEventCount -ne 0) { throw "MFT run $method reports dropped events." }
        if ([int64]$run.EnumeratedEntryCount -lt [int64]$run.DatasetEntryCount) { throw "MFT run $method enumerated fewer entries than its dataset contract." }
        if ([string]$method -eq 'MftCandidateMetadataQueriesZero1M' -and [int64]$run.DetailedMetadataQueryCount -ne 0) { throw 'The zero-candidate MFT run performed detailed metadata queries.' }
        if ([string]$method -eq 'MftCandidateMetadataQueriesSmall1M' -and ([int64]$run.CandidateCount -lt 1 -or [int64]$run.DetailedMetadataQueryCount -gt [int64]$run.CandidateCount)) { throw 'The small-candidate MFT run violated the candidate-only query bound.' }
    }

    $oneMillion = @($runs | Where-Object { [string]$_.Method -eq 'MftEnumerationImport1M' })[0]
    if ([int64]$oneMillion.DatasetEntryCount -lt 1000000 -or [int64]$oneMillion.EnumeratedEntryCount -lt 1000000) { throw 'The MFT 1M dataset did not meet the one-million-entry contract.' }

    foreach ($field in @('OperatingSystem', 'OsBuild', 'IsPhysicalMachine', 'CpuName', 'CpuLogicalCount', 'MemoryMiB', 'VhdxPath', 'VhdxFileIdentity', 'VhdxType', 'VhdxSizeGiB', 'DiskNumber', 'DiskUniqueId', 'VolumeUniqueId', 'VolumeGuidPath', 'DevicePath', 'MarkerPath', 'RunId', 'WorkloadRoot', 'WorkloadOraclePath', 'CreationEvidencePath', 'CreationEvidenceSha256', 'WriteMonitorEvidencePath', 'WriteMonitorEvidenceSha256', 'ProcessIdentities', 'DatasetEntryCount')) {
        $property = $evidence.Environment.PSObject.Properties[$field]
        if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) { throw "MFT evidence environment is missing $field." }
    }
    if (-not [bool]$evidence.Environment.IsPhysicalMachine -or [int64]$evidence.Environment.DatasetEntryCount -lt 1000000) { throw 'MFT environment does not prove a physical host and one-million-entry seed.' }
    return $evidence
}

function Write-NotExecuted([string]$Reason) {
    $manifest = [ordered]@{
        Schema = 'StorageChronicle.FullBenchmarkMatrixEvidence.v1'
        ExecutionStatus = 'not-executed'
        AcceptanceEligible = $false
        PortableOnly = [bool]$PortableOnly
        IncludeMft = [bool]$IncludeMft
        Configuration = $Configuration
        Project = $project
        StartedUtc = $runStartedUtc
        CompletedUtc = [DateTimeOffset]::UtcNow
        Host = [ordered]@{
            OperatingSystem = [Environment]::OSVersion.VersionString
            DotnetVersion = (& dotnet --version 2>$null)
            MftVolumeConfigured = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_MFT_VOLUME'))
        }
        MftEvidencePath = $MftEvidencePath
        MftCreationEvidencePath = $MftCreationEvidencePath
        MftWriteMonitorEvidencePath = $MftWriteMonitorEvidencePath
        Suites = @()
        Failure = $Reason
        EvidenceRoot = $runRoot
    }
    $manifestPath = Join-Path $runRoot 'full-matrix-manifest.json'
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -Encoding UTF8 -LiteralPath $manifestPath
    Write-Host "Full benchmark matrix was not executed: $Reason. Evidence: $manifestPath" -ForegroundColor Yellow
    exit 2
}

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    Write-NotExecuted "Benchmark project was not found: $project"
}

if ($IncludeMft -and $PortableOnly) {
    Write-NotExecuted 'IncludeMft and PortableOnly cannot be combined.'
}

if (-not $IncludeMft -and -not $PortableOnly) {
    Write-NotExecuted 'The full acceptance matrix requires -IncludeMft. Use -PortableOnly only for an explicitly non-acceptance diagnostic run.'
}

if ($IncludeMft) {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        Write-NotExecuted 'The full matrix includes WindowsMftBenchmarks and therefore requires Windows.'
    }
    $mftVolume = [Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_MFT_VOLUME')
    if ([string]::IsNullOrWhiteSpace($mftVolume)) {
        Write-NotExecuted 'The full matrix requires STORAGE_CHRONICLE_MFT_VOLUME to identify the dedicated NTFS seed device path.'
    }
    $mftMarker = [Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_MFT_MARKER_PATH')
    if ([string]::IsNullOrWhiteSpace($mftMarker)) {
        Write-NotExecuted 'The full matrix requires STORAGE_CHRONICLE_MFT_MARKER_PATH for the persistent MFT seed marker.'
    }
    try {
        if ([string]::IsNullOrWhiteSpace($MftCreationEvidencePath)) { throw 'A run-scoped creator evidence path is required via -MftCreationEvidencePath or STORAGE_CHRONICLE_MFT_CREATION_EVIDENCE_PATH.' }
        if ([string]::IsNullOrWhiteSpace($MftWriteMonitorEvidencePath)) { throw 'Independent process-attributed write-monitor evidence is required via -MftWriteMonitorEvidencePath or STORAGE_CHRONICLE_MFT_WRITE_MONITOR_EVIDENCE_PATH. Creator-authored intent/manifest alone is not independent provenance.' }
        $inventory = Get-MftPhysicalSeedInventory -MarkerPath $mftMarker -DevicePath $mftVolume
        $mftPreflight = Assert-MftPhysicalSeedInventory -Inventory $inventory
        $mftCreationEvidence = Assert-MftSeedCreationEvidence -Path $MftCreationEvidencePath -Inventory $inventory
        $mftWriteMonitorEvidence = Assert-MftIndependentWriteMonitorEvidence -Path $MftWriteMonitorEvidencePath -CreationEvidence $mftCreationEvidence -Inventory $inventory
        $mftEvidenceDirectory = Join-Path $runRoot 'WindowsMft'
        New-MftDirectoryCreateNew -Path $mftEvidenceDirectory | Out-Null
        $mftPhysicalPreflightPath = Join-Path $mftEvidenceDirectory 'mft-physical-preflight.json'
        Write-MftCreateNewJson -Path $mftPhysicalPreflightPath -Value ([ordered]@{ Schema = 'StorageChronicle.MftPhysicalSeedPreflight.v1'; Status = 'PASS'; AcceptanceEligible = $true; Environment = $mftPreflight })
    }
    catch {
        Write-NotExecuted "MFT seed provenance/preflight failed closed: $($_.Exception.Message)"
    }
}

$suites = @(
    [ordered]@{
        Name = 'CoreProjectionState'
        Filter = '*StorageChronicleBenchmarks*'
        ExpectedMethods = @('ReconstructSinglePointPath1M', 'GroupedGeneration100K', 'EventStackPage100K', 'PeriodDiff100K')
    },
    [ordered]@{
        Name = 'LargeFolderMove'
        Filter = '*LargeFolderMoveBenchmarks*'
        ExpectedMethods = @('RecordLargeFolderMove')
    },
    [ordered]@{
        Name = 'AppendAndCompression'
        Filter = '*StorageAppendBenchmarks*'
        ExpectedMethods = @('SegmentAppendAndSqliteIndex100K', 'FlushAndCloseCompressedSegment100K')
    },
    [ordered]@{
        Name = 'StorageAppend1M'
        Filter = '*SegmentAppendAndSqliteIndex1M*'
        ExpectedMethods = @('SegmentAppendAndSqliteIndex1M')
    },
    [ordered]@{
        Name = 'SqliteRecoveryAndQuery'
        Filter = '*SqliteRebuildBenchmarks*'
        ExpectedMethods = @('SqliteIndexRebuild100K', 'SqliteIndexedCount100K')
    },
    [ordered]@{
        Name = 'MediaManifest'
        Filter = '*MediaManifestBenchmarks*'
        ExpectedMethods = @('MediaManifestImport100K', 'MediaManifestReadAndValidate100K')
    },
    [ordered]@{
        Name = 'MediaSegment'
        Filter = '*MediaSegmentAppendBenchmarks*'
        ExpectedMethods = @('MediaSegmentAppend100K')
    }
)

if ($IncludeMft) {
    $suites += [ordered]@{
        Name = 'WindowsMft'
        Filter = '*WindowsMftBenchmarks*'
        ExpectedMethods = @('MftEnumerationImport10K', 'MftEnumerationImport100K', 'MftEnumerationImport1M', 'MftCandidateMetadataQueriesZero1M', 'MftCandidateMetadataQueriesSmall1M')
    }
}

$suiteResults = [System.Collections.Generic.List[object]]::new()
$failure = $null
$mftEvidence = $null
$savedBenchmarkEnvironment = @{}
$benchmarkProject = $project
$temporaryDrive = $null

if ($IncludeMft -and $null -ne $mftCreationEvidence -and $null -ne $mftWriteMonitorEvidence) {
    $benchmarkEnvironment = @{
        STORAGE_CHRONICLE_MFT_EVIDENCE_PATH = [IO.Path]::GetFullPath($MftEvidencePath)
        STORAGE_CHRONICLE_MFT_ARTIFACT_ROOT = [IO.Path]::GetFullPath($ArtifactRoot)
        STORAGE_CHRONICLE_MFT_VOLUME = [string]$mftPreflight.DevicePath
        STORAGE_CHRONICLE_MFT_MARKER_PATH = [string]$mftPreflight.MarkerPath
        STORAGE_CHRONICLE_MFT_PHYSICAL_HOST_OS = [string]$mftPreflight.OperatingSystem
        STORAGE_CHRONICLE_MFT_PHYSICAL_HOST_OS_BUILD = [string]$mftPreflight.OsBuild
        STORAGE_CHRONICLE_MFT_PHYSICAL_HOST_CPU_NAME = [string]$mftPreflight.CpuName
        STORAGE_CHRONICLE_MFT_PHYSICAL_HOST_CPU_COUNT = [string]$mftPreflight.CpuLogicalCount
        STORAGE_CHRONICLE_MFT_PHYSICAL_HOST_MEMORY_MIB = [string]$mftPreflight.MemoryMiB
        STORAGE_CHRONICLE_MFT_VHDX_PATH = [string]$mftPreflight.VhdxPath
        STORAGE_CHRONICLE_MFT_VHDX_TYPE = [string]$mftPreflight.VhdxType
        STORAGE_CHRONICLE_MFT_VHDX_SIZE_GIB = [string]$mftPreflight.VhdxSizeGiB
        STORAGE_CHRONICLE_MFT_DISK_NUMBER = [string]$mftPreflight.DiskNumber
        STORAGE_CHRONICLE_MFT_DISK_UNIQUE_ID = [string]$mftPreflight.DiskUniqueId
        STORAGE_CHRONICLE_MFT_VOLUME_UNIQUE_ID = [string]$mftPreflight.VolumeUniqueId
        STORAGE_CHRONICLE_MFT_VOLUME_GUID_PATH = [string]$mftPreflight.VolumeGuidPath
        STORAGE_CHRONICLE_MFT_VOLUME_LABEL = [string]$mftPreflight.VolumeLabel
        STORAGE_CHRONICLE_MFT_SEED_RUN_ID = [string]$mftPreflight.RunId
        STORAGE_CHRONICLE_MFT_SEED_ENTRY_COUNT = [string]$mftPreflight.DatasetEntryCount
        STORAGE_CHRONICLE_MFT_PREFLIGHT_SCHEMA = 'StorageChronicle.MftPhysicalSeedPreflight.v1'
        STORAGE_CHRONICLE_MFT_PREFLIGHT_PATH = [IO.Path]::GetFullPath($mftPhysicalPreflightPath)
        STORAGE_CHRONICLE_MFT_SEED_VHDX_FILE_IDENTITY = [string]$mftCreationEvidence.VhdxFileIdentity
        STORAGE_CHRONICLE_MFT_SEED_WORKLOAD_ROOT = [string]$mftCreationEvidence.WorkloadRoot
        STORAGE_CHRONICLE_MFT_SEED_WORKLOAD_ORACLE_PATH = [string]$mftCreationEvidence.WorkloadOraclePath
        STORAGE_CHRONICLE_MFT_CREATION_EVIDENCE_PATH = [IO.Path]::GetFullPath($MftCreationEvidencePath)
        STORAGE_CHRONICLE_MFT_CREATION_EVIDENCE_SHA256 = Get-MftSha256 -LiteralPath $MftCreationEvidencePath
        STORAGE_CHRONICLE_MFT_WRITE_MONITOR_EVIDENCE_PATH = [IO.Path]::GetFullPath($MftWriteMonitorEvidencePath)
        STORAGE_CHRONICLE_MFT_WRITE_MONITOR_EVIDENCE_SHA256 = Get-MftSha256 -LiteralPath $MftWriteMonitorEvidencePath
        STORAGE_CHRONICLE_MFT_SEED_PROCESS_IDENTITIES = ConvertTo-Json -InputObject @($mftCreationEvidence.Processes) -Depth 8 -Compress
    }
    foreach ($name in $benchmarkEnvironment.Keys) {
        $savedBenchmarkEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, [string]$benchmarkEnvironment[$name], 'Process')
    }
}

try {
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT -and $project.Length -gt 80) {
        $usedDriveLetters = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($drive in (Get-PSDrive -PSProvider FileSystem)) { [void]$usedDriveLetters.Add([string]$drive.Name) }
        $driveLetter = $null
        foreach ($candidate in @('Z', 'Y', 'X', 'W', 'V', 'U', 'T', 'S', 'R')) {
            if (-not $usedDriveLetters.Contains($candidate)) { $driveLetter = $candidate; break }
        }
        if ($null -eq $driveLetter) { throw 'No free drive letter is available for a temporary short-path mapping; benchmark execution was not started.' }
        & subst.exe ("{0}:" -f $driveLetter) $root
        if ($LASTEXITCODE -ne 0) { throw "Could not create temporary short-path mapping for the benchmark checkout (exit $LASTEXITCODE)." }
        $temporaryDrive = "{0}:" -f $driveLetter
        $benchmarkProject = "{0}\benchmarks\StorageChronicle.Benchmarks\StorageChronicle.Benchmarks.csproj" -f $temporaryDrive
        if (-not (Test-Path -LiteralPath $benchmarkProject -PathType Leaf)) { throw 'Temporary short-path mapping did not resolve the benchmark project.' }
    }

    foreach ($suite in $suites) {
        $suiteRoot = Join-Path $runRoot $suite.Name
        if (-not (Test-Path -LiteralPath $suiteRoot -PathType Container)) { New-MftDirectoryCreateNew -Path $suiteRoot | Out-Null }
        else { Assert-MftWorkflowNoReparsePath -Path $suiteRoot }
        $logPath = Join-Path $suiteRoot 'runner.log'
        $suiteResult = [ordered]@{
            Name = $suite.Name
            Filter = $suite.Filter
            Toolchain = 'DotNetCli'
            ExpectedMethods = @($suite.ExpectedMethods)
            Status = 'not-executed'
            ExitCode = $null
            ReportPaths = @()
            ActualMethods = @()
            Error = $null
        }

        try {
            $arguments = @('run', '--project', $benchmarkProject, '-c', $Configuration)
            if ($NoRestore) { $arguments += '--no-restore' }
            $arguments += @('--', '--filter', $suite.Filter, '--artifacts', $suiteRoot, '--exporters', 'json', 'markdown')
            Write-Host "Running BenchmarkDotNet suite: $($suite.Name)"
            & dotnet @arguments 2>&1 | Tee-Object -FilePath $logPath
            $exitCode = $LASTEXITCODE
            $suiteResult.ExitCode = $exitCode
            if ($exitCode -ne 0) {
                throw "BenchmarkDotNet exited with code $exitCode. See $logPath"
            }

            $reports = @(Get-ChildItem -LiteralPath $suiteRoot -Recurse -File -Filter '*full-compressed.json' -ErrorAction SilentlyContinue)
            if ($reports.Count -eq 0) {
                throw "No BenchmarkDotNet full-compressed JSON report was produced for $($suite.Name). See $logPath"
            }

            $allBenchmarks = [System.Collections.Generic.List[object]]::new()
            $reportPaths = [System.Collections.Generic.List[string]]::new()
            foreach ($report in $reports) {
                $reportObject = Get-Content -Raw -Encoding UTF8 -LiteralPath $report.FullName | ConvertFrom-Json
                foreach ($benchmark in @($reportObject.Benchmarks)) { $allBenchmarks.Add($benchmark) }
                $reportPaths.Add($report.FullName)
            }

            $actualMethods = @($allBenchmarks | ForEach-Object { $_.MethodTitle } | Sort-Object -Unique)
            $suiteResult.ReportPaths = @($reportPaths)
            $suiteResult.ActualMethods = @($actualMethods)

            foreach ($expectedMethod in $suite.ExpectedMethods) {
                $matches = @($allBenchmarks | Where-Object {
                    $_.MethodTitle -eq $expectedMethod -and $null -ne $_.Statistics -and $null -ne $_.Statistics.Mean
                })
                if ($matches.Count -ne 1) {
                    throw "Expected exactly one successful BenchmarkDotNet result for $($suite.Name).$expectedMethod, found $($matches.Count)."
                }
            }

            $suiteResult.Status = 'passed'
        }
        catch {
            $suiteResult.Status = 'failed'
            $suiteResult.Error = $_.Exception.Message
            $suiteResults.Add([pscustomobject]$suiteResult)
            throw
        }

        $suiteResults.Add([pscustomobject]$suiteResult)
    }
    if ($IncludeMft) {
        $mftEvidence = Assert-MftEvidence -Path $MftEvidencePath
        $creationHash = Get-MftSha256 -LiteralPath $MftCreationEvidencePath
        $monitorHash = Get-MftSha256 -LiteralPath $MftWriteMonitorEvidencePath
        foreach ($pair in @(
            @('SeedVhdxFileIdentity', [string]$mftCreationEvidence.VhdxFileIdentity),
            @('SeedWorkloadRoot', [string]$mftCreationEvidence.WorkloadRoot),
            @('SeedWorkloadOraclePath', [string]$mftCreationEvidence.WorkloadOraclePath),
            @('SeedCreationEvidencePath', [IO.Path]::GetFullPath($MftCreationEvidencePath)),
            @('SeedCreationEvidenceSha256', $creationHash),
            @('SeedWriteMonitorEvidencePath', [IO.Path]::GetFullPath($MftWriteMonitorEvidencePath)),
            @('SeedWriteMonitorEvidenceSha256', $monitorHash),
            @('SeedProcessIdentities', @($mftCreationEvidence.Processes)))) {
            $mftEvidence.Environment | Add-Member -MemberType NoteProperty -Name $pair[0] -Value $pair[1] -Force
        }
    }
}
catch {
    $failure = $_.Exception.Message
}
finally {
    if ($null -ne $temporaryDrive) {
        & subst.exe $temporaryDrive /D
        if ($LASTEXITCODE -ne 0) {
            $mappingError = "Failed to release temporary benchmark path mapping $temporaryDrive (exit $LASTEXITCODE)."
            if ($null -eq $failure) { $failure = $mappingError } else { $failure = "$failure; $mappingError" }
        }
        $temporaryDrive = $null
    }
    $acceptanceEligible = $null -eq $failure -and $IncludeMft -and (@($suiteResults | Where-Object Status -ne 'passed').Count -eq 0)
    $manifest = [ordered]@{
        Schema = 'StorageChronicle.FullBenchmarkMatrixEvidence.v1'
        ExecutionStatus = if ($null -eq $failure) { 'completed' } else { 'failed' }
        AcceptanceEligible = [bool]$acceptanceEligible
        PortableOnly = [bool]$PortableOnly
        IncludeMft = [bool]$IncludeMft
        Configuration = $Configuration
        Project = $project
        StartedUtc = $runStartedUtc
        CompletedUtc = [DateTimeOffset]::UtcNow
        Host = [ordered]@{
            OperatingSystem = [Environment]::OSVersion.VersionString
            WindowsProductName = $windowsProductName
            DotnetVersion = (& dotnet --version 2>$null)
            MftVolumeConfigured = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_MFT_VOLUME'))
            MftVolumeLabel = [Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_MFT_VOLUME_LABEL')
            MftMarkerPath = [Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_MFT_MARKER_PATH')
            MftPhysicalPreflightPath = $mftPhysicalPreflightPath
            MftCreationEvidencePath = $MftCreationEvidencePath
            MftCreationEvidenceSha256 = if ($IncludeMft -and $null -ne $mftCreationEvidence) { Get-MftSha256 -LiteralPath $MftCreationEvidencePath } else { $null }
            MftWriteMonitorEvidencePath = $MftWriteMonitorEvidencePath
            MftWriteMonitorEvidenceSha256 = if ($IncludeMft -and $null -ne $mftWriteMonitorEvidence) { Get-MftSha256 -LiteralPath $MftWriteMonitorEvidencePath } else { $null }
        }
        MftEvidencePath = $MftEvidencePath
        MftEvidence = if ($null -ne $mftEvidence) { $mftEvidence } else { $null }
        Suites = @($suiteResults)
        Failure = $failure
        EvidenceRoot = $runRoot
    }
    $manifestPath = Join-Path $runRoot 'full-matrix-manifest.json'
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -Encoding UTF8 -LiteralPath $manifestPath
    foreach ($name in $savedBenchmarkEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, [string]$savedBenchmarkEnvironment[$name], 'Process') }
    Write-Host "Benchmark matrix evidence: $manifestPath"

}

if ($null -ne $failure) {
    Write-Error "Full BenchmarkDotNet matrix failed or is not acceptance-eligible: $failure"
    exit 1
}

if (-not $IncludeMft) {
    Write-Warning 'Portable benchmark suites completed, but this run is not a full acceptance matrix because MFT was not executed.'
}
else {
    Write-Output 'Full BenchmarkDotNet matrix completed with every required suite and method represented in validated JSON reports.'
}
exit 0
