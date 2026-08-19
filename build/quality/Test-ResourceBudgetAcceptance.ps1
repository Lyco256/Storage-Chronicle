[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProcessId,
    [string]$AgentPipeName = 'StorageChronicle.Agent',
    [string]$Configuration = 'Release',
    [ValidateRange(1, 600)][int]$DurationSeconds = 600,
    [ValidateRange(50, 10000)][int]$IntervalMilliseconds = 1000,
    [ValidateRange(1, 4096)][int]$MaxQueueDepth = 4096,
    [ValidateRange(1, 4096)][double]$MaxPrivateMiB = 50,
    [ValidateRange(0.001, 100)][double]$MaxCpuPercent = 0.5,
    [Parameter(Mandatory = $false)][string]$QuietPeriodEvidencePath,
    [switch]$Diagnostic,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$existingScript = Join-Path $PSScriptRoot 'Test-ResourceBudget.ps1'
$artifactDirectory = Join-Path $root 'artifacts/quality/resources'

if (-not (Test-Path -LiteralPath $existingScript -PathType Leaf)) {
    throw "The existing resource-budget script was not found: $existingScript"
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Resource-budget acceptance requires Windows.'
}

if ($DurationSeconds -ne 600 -and -not $Diagnostic) {
    throw 'Acceptance mode requires exactly 600 seconds. Use -Diagnostic only for non-acceptance diagnostics.'
}

if (-not $Diagnostic -and ($MaxPrivateMiB -ne 50 -or $MaxCpuPercent -ne 0.5)) {
    throw 'Acceptance mode fixes the R-03 thresholds at 50 MiB private working set and 0.5% average CPU; relaxed thresholds are not permitted.'
}

$resourceEnvironment = [ordered]@{}
try {
    $operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
    $computerSystem = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
    $model = [string]$computerSystem.Model
    $manufacturer = [string]$computerSystem.Manufacturer
    $knownVirtualMachine = ($model + ' ' + $manufacturer) -match '(?i)(virtual|vmware|virtualbox|kvm|qemu|xen|hyper-v|parallels|bhyve|amazon ec2|google compute|azure)'
    $resourceEnvironment = [ordered]@{
        ProductName = [string]$operatingSystem.Caption
        DisplayVersion = if ($null -ne $operatingSystem.PSObject.Properties['DisplayVersion']) { [string]$operatingSystem.DisplayVersion } else { '' }
        Build = [string]$operatingSystem.BuildNumber
        Architecture = if ([Environment]::Is64BitOperatingSystem) { 'x64' } else { 'x86' }
        Manufacturer = $manufacturer
        Model = $model
        HypervisorPresent = if ($null -ne $computerSystem.PSObject.Properties['HypervisorPresent']) { [bool]$computerSystem.HypervisorPresent } else { $null }
        IsPhysicalMachine = -not $knownVirtualMachine
        Diagnostic = [bool]$Diagnostic
    }
}
catch {
    if (-not $Diagnostic) { throw "Could not establish the resource acceptance environment: $($_.Exception.Message)" }
    $resourceEnvironment = [ordered]@{ Diagnostic = $true; EnvironmentProbeError = $_.Exception.Message; IsPhysicalMachine = $false }
}

if (-not $Diagnostic -and (
        [string]$resourceEnvironment.ProductName -notmatch 'Windows 11' -or
        [string]$resourceEnvironment.Architecture -ne 'x64' -or
        -not [bool]$resourceEnvironment.IsPhysicalMachine)) {
    throw 'Formal resource acceptance requires a Windows 11 x64 physical release machine; VM or other host evidence is not eligible.'
}

$processIds = @($ProcessId -split '[,;]' | ForEach-Object {
    $parsed = 0
    if (-not [int]::TryParse($_.Trim(), [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed) -or $parsed -le 0) {
        throw "Invalid process ID: $_"
    }
    $parsed
} | Sort-Object -Unique)

if ($processIds.Count -lt 2 -and -not $Diagnostic) {
    throw 'Acceptance mode requires both the Agent and Session Agent process IDs.'
}

if (-not $Diagnostic -and [string]::IsNullOrWhiteSpace($QuietPeriodEvidencePath)) {
    throw 'Acceptance mode requires -QuietPeriodEvidencePath. The existing resource script does not independently prove that the final five minutes had no bulk events.'
}

if (-not [string]::IsNullOrWhiteSpace($QuietPeriodEvidencePath) -and -not (Test-Path -LiteralPath $QuietPeriodEvidencePath -PathType Leaf)) {
    throw "Quiet-period evidence was not found: $QuietPeriodEvidencePath"
}

if ($ValidateOnly) {
    Write-Output 'Resource acceptance wrapper validation passed; no process was started and no acceptance result was produced.'
    exit 0
}

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ', [Globalization.CultureInfo]::InvariantCulture)
$evidencePath = Join-Path $artifactDirectory ("acceptance-" + ($processIds -join '-') + '-' + $stamp + '.json')
$stdoutPath = Join-Path $artifactDirectory ("acceptance-" + ($processIds -join '-') + '-' + $stamp + '.stdout.log')
$stderrPath = Join-Path $artifactDirectory ("acceptance-" + ($processIds -join '-') + '-' + $stamp + '.stderr.log')

function Get-ProcessIdentity {
    param([int]$Id)

    try {
        $process = [Diagnostics.Process]::GetProcessById($Id)
        try {
            $startTime = $process.StartTime.ToUniversalTime().ToString('o')
        }
        catch {
            $startTime = $null
        }
        [pscustomobject]@{
            Id = $Id
            Present = $true
            Name = $process.ProcessName
            StartTimeUtc = $startTime
        }
        $process.Dispose()
    }
    catch {
        [pscustomobject]@{
            Id = $Id
            Present = $false
            Name = $null
            StartTimeUtc = $null
        }
    }
}

$initialIdentities = @($processIds | ForEach-Object { Get-ProcessIdentity -Id $_ })
if (@($initialIdentities | Where-Object { -not $_.Present }).Count -gt 0) {
    throw 'One or more target processes exited before the supervised measurement started.'
}
if (-not $Diagnostic -and @($initialIdentities | Where-Object { [string]::IsNullOrWhiteSpace($_.StartTimeUtc) }).Count -gt 0) {
    throw 'The target process start time could not be read; process identity cannot be proven fail-closed.'
}

$startInfo = [Diagnostics.ProcessStartInfo]::new()
$hostExecutable = Join-Path $PSHOME 'powershell.exe'
if (-not (Test-Path -LiteralPath $hostExecutable -PathType Leaf)) { $hostExecutable = 'pwsh' }
$startInfo.FileName = $hostExecutable
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$childArguments = @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $existingScript,
    '-ProcessId', $ProcessId, '-AgentPipeName', $AgentPipeName, '-Configuration', $Configuration,
    '-DurationSeconds', $DurationSeconds, '-IntervalMilliseconds', $IntervalMilliseconds,
    '-MaxQueueDepth', $MaxQueueDepth, '-MaxPrivateMiB', $MaxPrivateMiB,
    '-MaxCpuPercent', $MaxCpuPercent)
if ($Diagnostic) { $childArguments += '-Diagnostic' }

$argumentListProperty = [Diagnostics.ProcessStartInfo].GetProperty('ArgumentList')
if ($null -ne $argumentListProperty -and $null -ne $startInfo.ArgumentList) {
    foreach ($argument in $childArguments) { [void]$startInfo.ArgumentList.Add([string]$argument) }
}
else {
    $quotedArguments = foreach ($argument in $childArguments) {
        $text = [string]$argument
        if ($text -notmatch '[\s"]') { $text; continue }
        $escaped = $text -replace '(\\*)"', '$1$1\\"'
        $escaped = $escaped -replace '(\\+)$', '$1$1'
        '"' + $escaped + '"'
    }
    $startInfo.Arguments = $quotedArguments -join ' '
}

$runner = [Diagnostics.Process]::new()
$runner.StartInfo = $startInfo
$lifecycleSamples = [System.Collections.Generic.List[object]]::new()
$lifecycleErrors = [System.Collections.Generic.List[string]]::new()
$runnerExitCode = $null
$runnerStartedUtc = [DateTimeOffset]::UtcNow
$lifecycleHealthy = $true
$failure = $null

try {
    if (-not $runner.Start()) { throw 'The existing resource-budget script could not be started.' }
    while (-not $runner.HasExited) {
        $sample = @($processIds | ForEach-Object { Get-ProcessIdentity -Id $_ })
        $lifecycleSamples.Add([pscustomobject]@{ RecordedUtc = [DateTimeOffset]::UtcNow; Processes = $sample })
        foreach ($current in $sample) {
            $initial = @($initialIdentities | Where-Object Id -eq $current.Id)[0]
            if (-not $current.Present -or $current.StartTimeUtc -ne $initial.StartTimeUtc) {
                $lifecycleHealthy = $false
                $lifecycleErrors.Add("Process $($current.Id) exited or changed identity during measurement.")
            }
        }
        if (-not $lifecycleHealthy) {
            try { $runner.Kill() } catch { $lifecycleErrors.Add("Could not stop the child resource script: $($_.Exception.Message)") }
            break
        }
        Start-Sleep -Milliseconds $IntervalMilliseconds
    }

    $runner.WaitForExit()
    $runnerExitCode = $runner.ExitCode
    $stdout = $runner.StandardOutput.ReadToEnd()
    $stderr = $runner.StandardError.ReadToEnd()
    Set-Content -Encoding UTF8 -LiteralPath $stdoutPath -Value $stdout
    Set-Content -Encoding UTF8 -LiteralPath $stderrPath -Value $stderr

    $finalIdentities = @($processIds | ForEach-Object { Get-ProcessIdentity -Id $_ })
    $lifecycleSamples.Add([pscustomobject]@{ RecordedUtc = [DateTimeOffset]::UtcNow; Processes = $finalIdentities })
    foreach ($current in $finalIdentities) {
        $initial = @($initialIdentities | Where-Object Id -eq $current.Id)[0]
        if (-not $current.Present -or $current.StartTimeUtc -ne $initial.StartTimeUtc) {
            $lifecycleHealthy = $false
            $lifecycleErrors.Add("Process $($current.Id) was not the original process at measurement completion.")
        }
    }

    $matchingResults = @(Get-ChildItem -LiteralPath $artifactDirectory -Filter ("process-" + ($processIds -join '-') + '-*.json') -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -ge $runnerStartedUtc.UtcDateTime } |
        Sort-Object LastWriteTimeUtc -Descending)
    $resultPath = if ($matchingResults.Count -gt 0) { $matchingResults[0].FullName } else { $null }
    $result = $null
    $evidenceChecks = [ordered]@{}

    if ($null -eq $resultPath) {
        $evidenceChecks.ResultFilePresent = $false
        $failure = 'The existing resource-budget script did not produce a new JSON result.'
    }
    else {
        $result = Get-Content -Raw -Encoding UTF8 -LiteralPath $resultPath | ConvertFrom-Json
        $boundary = $result.MeasurementBoundary
        $samples = @($result.Samples)
        $expectedSamples = [Math]::Max(2, [Math]::Floor(($DurationSeconds * 1000) / $IntervalMilliseconds))
        $minimumSamples = [Math]::Max(2, [Math]::Floor($expectedSamples * 0.8))
        $spanSeconds = if ($samples.Count -ge 2) {
            ([DateTimeOffset]::Parse($samples[-1].RecordedUtc) - [DateTimeOffset]::Parse($samples[0].RecordedUtc)).TotalSeconds
        } else { 0 }
        $boundarySpanSeconds = ([DateTimeOffset]::Parse($boundary.CompletedUtc) - [DateTimeOffset]::Parse($boundary.StartedUtc)).TotalSeconds
        $resultIds = @($result.ProcessIds | ForEach-Object { [int]$_ } | Sort-Object -Unique)
        $expectedIds = @($processIds | ForEach-Object { [int]$_ } | Sort-Object -Unique)
        $idsMatch = @((Compare-Object -ReferenceObject $expectedIds -DifferenceObject $resultIds)).Count -eq 0
        $evidenceChecks = [ordered]@{
            ResultFilePresent = $true
            ResultProcessIdsMatch = $idsMatch
            AcceptanceEligible = [bool]$result.AcceptanceEligible
            ExistingScriptExitCodeZero = $runnerExitCode -eq 0
            BoundaryDurationIs600 = [int]$boundary.DurationSeconds -eq 600
            BoundaryIsNonDiagnostic = -not [bool]$boundary.Diagnostic
            BoundarySampleSpanSeconds = $boundarySpanSeconds
            BoundarySpanComplete = $boundarySpanSeconds -ge ($DurationSeconds - (2 * $IntervalMilliseconds / 1000.0))
            ResourceSampleCountMatches = [int]$result.SampleCount -eq $samples.Count
            ResourceSampleCount = $samples.Count
            MinimumRequiredResourceSamples = $minimumSamples
            ResourceSamplingComplete = $samples.Count -ge $minimumSamples
            ResourceSampleSpanSeconds = $spanSeconds
            ResourceSampleSpanComplete = $spanSeconds -ge ($DurationSeconds - (2 * $IntervalMilliseconds / 1000.0))
            DiskWriteCounterPresent = $null -ne $result.DiskWriteBytes
            PrivateMemoryLimitNotExceeded = -not [bool]$result.PrivateMemoryLimitExceeded
            CpuLimitNotExceeded = -not [bool]$result.CpuLimitExceeded
            QueueSamplingComplete = [bool]$result.QueueDepth.SamplingComplete
            QueueSampleCountMatches = [int]$result.QueueDepth.SampleCount -eq @($result.QueueDepth.Samples).Count
            QueueMissedSamples = [int]$result.QueueDepth.MissedSamples
            QueueHasNoMissedSamples = [int]$result.QueueDepth.MissedSamples -eq 0
            QueueLimitNotExceeded = -not [bool]$result.QueueDepth.QueueDepthLimitExceeded
            TargetLifecycleStable = $lifecycleHealthy
        }

        if (-not $Diagnostic) {
            if ($null -eq $QuietPeriodEvidencePath) {
                $evidenceChecks.QuietPeriodEvidencePresent = $false
            }
            else {
                $quiet = Get-Content -Raw -Encoding UTF8 -LiteralPath $QuietPeriodEvidencePath | ConvertFrom-Json
                if ([string]$quiet.Schema -ne 'StorageChronicle.ResourceQuietWitness.v1' -or
                    [string]$quiet.Status -ne 'PASSED' -or
                    -not [bool]$quiet.AcceptanceEligible) {
                    throw 'Quiet-period evidence must be a passed StorageChronicle.ResourceQuietWitness.v1 artifact.'
                }
                $quietSeconds = [double]$quiet.QuietPeriodSeconds
                $bulkEvents = [int]$quiet.BulkEventCount
                $queueOverruns = [int]$quiet.QueueOverrunCount
                $quietStartedUtc = [DateTimeOffset]::Parse($quiet.QuietPeriodStartedUtc)
                $quietCompletedUtc = [DateTimeOffset]::Parse($quiet.QuietPeriodCompletedUtc)
                $boundaryStartedUtc = [DateTimeOffset]::Parse($boundary.StartedUtc)
                $boundaryCompletedUtc = [DateTimeOffset]::Parse($boundary.CompletedUtc)
                $quietSpanSeconds = ($quietCompletedUtc - $quietStartedUtc).TotalSeconds
                $quietWindowValid = $quietStartedUtc -ge $boundaryStartedUtc -and $quietCompletedUtc -le $boundaryCompletedUtc.AddSeconds(5) -and $quietCompletedUtc -gt $quietStartedUtc
                $quietEndedNearMeasurement = $quietCompletedUtc -ge $boundaryCompletedUtc.AddSeconds(-[Math]::Max(5, 2 * $IntervalMilliseconds / 1000.0))
                $evidenceChecks.QuietPeriodEvidencePresent = $true
                $evidenceChecks.QuietPeriodSeconds = $quietSeconds
                $evidenceChecks.BulkEventCount = $bulkEvents
                $evidenceChecks.QuietPeriodStartedUtc = $quietStartedUtc
                $evidenceChecks.QuietPeriodCompletedUtc = $quietCompletedUtc
                $evidenceChecks.QueueOverrunCount = $queueOverruns
                $evidenceChecks.QueueOverrunCountIsZero = $queueOverruns -eq 0
                $evidenceChecks.ReconciliationActiveTimeSeconds = [double]$quiet.ReconciliationActiveTimeSeconds
                $evidenceChecks.BenchmarkWorkloadProcessesAbsent = [bool]$quiet.BenchmarkWorkloadProcessesAbsent
                $evidenceChecks.BuildTestProcessesAbsent = [bool]$quiet.BuildTestProcessesAbsent
                $evidenceChecks.QuietPeriodMeasuredSeconds = $quietSpanSeconds
                $evidenceChecks.QuietPeriodDurationMatches = [Math]::Abs($quietSpanSeconds - $quietSeconds) -le [Math]::Max(2, 2 * $IntervalMilliseconds / 1000.0)
                $evidenceChecks.QuietPeriodWindowValid = $quietWindowValid
                $evidenceChecks.QuietPeriodEndedNearMeasurement = $quietEndedNearMeasurement
                $evidenceChecks.QuietPeriodValid = $quietSeconds -ge 300 -and $bulkEvents -eq 0 -and $queueOverruns -eq 0 -and $evidenceChecks.BenchmarkWorkloadProcessesAbsent -and $evidenceChecks.BuildTestProcessesAbsent -and $evidenceChecks.QuietPeriodDurationMatches -and $quietWindowValid -and $quietEndedNearMeasurement
            }
        }

        $requiredCheckNames = @(
            'ResultFilePresent', 'ResultProcessIdsMatch',
            'ResourceSampleCountMatches', 'ResourceSamplingComplete', 'ResourceSampleSpanComplete',
            'DiskWriteCounterPresent', 'PrivateMemoryLimitNotExceeded', 'CpuLimitNotExceeded',
            'QueueSamplingComplete', 'QueueSampleCountMatches', 'QueueHasNoMissedSamples',
            'QueueLimitNotExceeded', 'TargetLifecycleStable')
        if (-not $Diagnostic) {
            $requiredCheckNames += @(
                'ExistingScriptExitCodeZero', 'AcceptanceEligible', 'BoundaryDurationIs600', 'BoundaryIsNonDiagnostic',
                'BoundarySpanComplete', 'QuietPeriodEvidencePresent', 'QuietPeriodValid')
        }
        $failedChecks = @($requiredCheckNames | Where-Object { -not $evidenceChecks.Contains($_) -or $evidenceChecks[$_] -ne $true })
        if ($failedChecks.Count -gt 0 -or -not $lifecycleHealthy -or (-not $Diagnostic -and $runnerExitCode -ne 0)) {
            $failure = 'Resource acceptance evidence is incomplete, failed, or not fail-closed.'
        }
    }

    $evidence = [ordered]@{
        Schema = 'StorageChronicle.ResourceBudgetAcceptanceEvidence.v1'
        ExecutionStatus = if ($null -eq $failure) { 'passed' } else { 'failed' }
        AcceptanceEligible = $null -eq $failure -and -not $Diagnostic
        ExistingScript = $existingScript
        ExistingScriptExitCode = $runnerExitCode
        Configuration = $Configuration
        ResultPath = $resultPath
        QuietPeriodEvidencePath = $QuietPeriodEvidencePath
        Environment = $resourceEnvironment
        InitialProcesses = $initialIdentities
        LifecycleHealthy = $lifecycleHealthy
        LifecycleErrors = @($lifecycleErrors)
        LifecycleSamples = @($lifecycleSamples)
        EvidenceChecks = [pscustomobject]$evidenceChecks
        RunnerStartedUtc = $runnerStartedUtc
        CompletedUtc = [DateTimeOffset]::UtcNow
        StandardOutputPath = $stdoutPath
        StandardErrorPath = $stderrPath
        Failure = $failure
    }
    $evidence | ConvertTo-Json -Depth 12 | Set-Content -Encoding UTF8 -LiteralPath $evidencePath
    Write-Host "Resource acceptance evidence: $evidencePath"
}
catch {
    $failure = $_.Exception.Message
    $fallback = [ordered]@{
        Schema = 'StorageChronicle.ResourceBudgetAcceptanceEvidence.v1'
        ExecutionStatus = 'failed'
        AcceptanceEligible = $false
        ExistingScript = $existingScript
        ExistingScriptExitCode = $runnerExitCode
        Configuration = $Configuration
        Environment = $resourceEnvironment
        InitialProcesses = $initialIdentities
        LifecycleHealthy = $lifecycleHealthy
        LifecycleErrors = @($lifecycleErrors)
        LifecycleSamples = @($lifecycleSamples)
        CompletedUtc = [DateTimeOffset]::UtcNow
        Failure = $failure
    }
    $fallback | ConvertTo-Json -Depth 12 | Set-Content -Encoding UTF8 -LiteralPath $evidencePath
    Write-Error "Resource acceptance supervision failed: $failure"
    exit 1
}
finally {
    if ($runner -is [IDisposable]) { $runner.Dispose() }
}

if ($null -ne $failure) {
    Write-Error "Resource acceptance failed: $failure"
    exit 1
}

Write-Output 'Resource acceptance completed with stable target process identities and complete evidence.'
exit 0
