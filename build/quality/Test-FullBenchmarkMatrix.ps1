[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$NoRestore,
    [switch]$IncludeMft,
    [switch]$PortableOnly,
    [string]$ArtifactRoot,
    [string]$MftEvidencePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $root 'benchmarks/StorageChronicle.Benchmarks/StorageChronicle.Benchmarks.csproj'

if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $root 'artifacts/benchmarks'
}
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ', [Globalization.CultureInfo]::InvariantCulture)
$runStartedUtc = [DateTimeOffset]::UtcNow
$runRoot = Join-Path $ArtifactRoot ("full-matrix-" + $stamp)
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

if ([string]::IsNullOrWhiteSpace($MftEvidencePath)) {
    $MftEvidencePath = Join-Path (Join-Path $runRoot 'WindowsMft') 'mft-evidence.json'
}
$oldMftEvidencePath = $null

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

    foreach ($field in @('OperatingSystem', 'OsBuild', 'VmCpuCount', 'VmMemoryMiB', 'VhdxType', 'VhdxSizeGiB')) {
        $property = $evidence.Environment.PSObject.Properties[$field]
        if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) { throw "MFT evidence environment is missing $field." }
    }
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
        Write-NotExecuted 'The full matrix requires STORAGE_CHRONICLE_MFT_VOLUME to identify a dedicated NTFS capability volume.'
    }
    if (-not [string]::Equals([Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_MFT_VOLUME_LABEL'), 'SC_TEST_MFT_VOLUME', [StringComparison]::Ordinal)) {
        Write-NotExecuted 'The full matrix requires STORAGE_CHRONICLE_MFT_VOLUME_LABEL=SC_TEST_MFT_VOLUME; host/system volumes are not accepted.'
    }
    $mftMarker = [Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_MFT_MARKER_PATH')
    if ([string]::IsNullOrWhiteSpace($mftMarker) -or -not (Test-Path -LiteralPath $mftMarker -PathType Leaf)) {
        Write-NotExecuted 'The full matrix requires an existing STORAGE_CHRONICLE_MFT_MARKER_PATH from the dedicated TestLab data volume.'
    }
    $markerValue = Get-Content -Raw -Encoding UTF8 -LiteralPath $mftMarker | ConvertFrom-Json
    if ([string]$markerValue.Schema -ne 'StorageChronicle.TestLabDataMarker.v1' -or [string]$markerValue.Role -ne 'Mft' -or [string]$markerValue.VolumeLabel -ne 'SC_TEST_MFT_VOLUME' -or [string]$markerValue.FileSystem -ine 'NTFS') {
        Write-NotExecuted 'The MFT marker must prove the Mft role, SC_TEST_MFT_VOLUME label, and NTFS filesystem.'
    }
    if ($mftVolume -match '(?i)(^|[\\:])C:') {
        Write-NotExecuted 'The full matrix refuses C: and host/system MFT device paths.'
    }
    foreach ($name in @('STORAGE_CHRONICLE_MFT_VM_CPU_COUNT', 'STORAGE_CHRONICLE_MFT_VM_MEMORY_MIB', 'STORAGE_CHRONICLE_MFT_VHDX_TYPE', 'STORAGE_CHRONICLE_MFT_VHDX_SIZE_GIB')) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { Write-NotExecuted "The full matrix requires $name from the measured TestLab environment." }
    }

    $oldMftEvidencePath = [Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_MFT_EVIDENCE_PATH', 'Process')
    [Environment]::SetEnvironmentVariable('STORAGE_CHRONICLE_MFT_EVIDENCE_PATH', [IO.Path]::GetFullPath($MftEvidencePath), 'Process')
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

try {
    foreach ($suite in $suites) {
        $suiteRoot = Join-Path $runRoot $suite.Name
        New-Item -ItemType Directory -Force -Path $suiteRoot | Out-Null
        $logPath = Join-Path $suiteRoot 'runner.log'
        $suiteResult = [ordered]@{
            Name = $suite.Name
            Filter = $suite.Filter
            ExpectedMethods = @($suite.ExpectedMethods)
            Status = 'not-executed'
            ExitCode = $null
            ReportPaths = @()
            ActualMethods = @()
            Error = $null
        }

        try {
            $arguments = @('run', '--project', $project, '-c', $Configuration)
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
    }
}
catch {
    $failure = $_.Exception.Message
}
finally {
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
            DotnetVersion = (& dotnet --version 2>$null)
            MftVolumeConfigured = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_MFT_VOLUME'))
            MftVolumeLabel = [Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_MFT_VOLUME_LABEL')
            MftMarkerPath = [Environment]::GetEnvironmentVariable('STORAGE_CHRONICLE_MFT_MARKER_PATH')
        }
        MftEvidencePath = $MftEvidencePath
        MftEvidence = if ($null -ne $mftEvidence) { $mftEvidence } else { $null }
        Suites = @($suiteResults)
        Failure = $failure
        EvidenceRoot = $runRoot
    }
    $manifestPath = Join-Path $runRoot 'full-matrix-manifest.json'
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -Encoding UTF8 -LiteralPath $manifestPath
    Write-Host "Benchmark matrix evidence: $manifestPath"

    if ($IncludeMft) {
        if ($null -ne $oldMftEvidencePath) {
            [Environment]::SetEnvironmentVariable('STORAGE_CHRONICLE_MFT_EVIDENCE_PATH', $oldMftEvidencePath, 'Process')
        } else {
            [Environment]::SetEnvironmentVariable('STORAGE_CHRONICLE_MFT_EVIDENCE_PATH', $null, 'Process')
        }
    }
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
