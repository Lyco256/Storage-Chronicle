[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$AgentPipeName = 'StorageChronicle.Agent',
    [ValidateRange(300, 3600)][int]$DurationSeconds = 300,
    [ValidateRange(100, 10000)][int]$IntervalMilliseconds = 1000,
    [ValidateRange(1, 1048576)][int]$MaxQueueDepth = 4096,
    [string[]]$ForbiddenProcessName = @(
        'StorageChronicle.FileMutationWorkload',
        'StorageChronicle.Benchmarks',
        'dotnet',
        'msbuild',
        'vstest.console',
        'testhost'
    )
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The quiet witness requires Windows because it independently samples the Agent named pipe.'
}
if ([string]::IsNullOrWhiteSpace($AgentPipeName)) { throw 'AgentPipeName must not be empty.' }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { throw 'OutputPath must not be empty.' }

$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $fullOutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$witnessProcessId = [Diagnostics.Process]::GetCurrentProcess().Id

function Read-ExactBytes {
    param(
        [Parameter(Mandatory = $true)][System.IO.Stream]$Stream,
        [Parameter(Mandatory = $true)][byte[]]$Buffer
    )

    $timeout = [Threading.CancellationTokenSource]::new(2000)
    try {
        $offset = 0
        while ($offset -lt $Buffer.Length) {
            $read = $Stream.ReadAsync($Buffer, $offset, $Buffer.Length - $offset, $timeout.Token).GetAwaiter().GetResult()
            if ($read -le 0) { throw 'The Agent health named pipe closed before the response frame was complete.' }
            $offset += $read
        }
    }
    finally {
        $timeout.Dispose()
    }
}

function Get-AgentHealth {
    $pipe = $null
    try {
        $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $AgentPipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::None)
        $pipe.Connect(2000)
        $requestJson = '{"Protocol":{"Major":1,"Minor":0},"MessageType":"AgentHealthRequest","Payload":{}}'
        $payload = [Text.Encoding]::UTF8.GetBytes($requestJson)
        $frame = [byte[]]::new(4 + $payload.Length)
        [BitConverter]::GetBytes([int32]$payload.Length).CopyTo($frame, 0)
        [Array]::Copy($payload, 0, $frame, 4, $payload.Length)
        $timeout = [Threading.CancellationTokenSource]::new(2000)
        try {
            $pipe.WriteAsync($frame, 0, $frame.Length, $timeout.Token).GetAwaiter().GetResult()
            $pipe.FlushAsync($timeout.Token).GetAwaiter().GetResult()
        }
        finally {
            $timeout.Dispose()
        }

        $header = [byte[]]::new(4)
        Read-ExactBytes -Stream $pipe -Buffer $header
        $length = [BitConverter]::ToInt32($header, 0)
        if ($length -le 0 -or $length -gt 8MB) { throw "The Agent health response frame length $length is outside the IPC limit." }
        $responseBytes = [byte[]]::new($length)
        Read-ExactBytes -Stream $pipe -Buffer $responseBytes
        $response = [Text.Encoding]::UTF8.GetString($responseBytes) | ConvertFrom-Json
        if ([string]$response.MessageType -ne 'AgentHealth') { throw "The Agent health pipe returned message type '$($response.MessageType)' instead of AgentHealth." }
        if ($null -eq $response.Payload.PSObject.Properties['LastSequence'] -or
            $null -eq $response.Payload.PSObject.Properties['QueueDepth'] -or
            $null -eq $response.Payload.PSObject.Properties['PendingReconciliations']) {
            throw 'The Agent health response did not contain the independent quiet-witness counters.'
        }

        $lastSequence = [int64]$response.Payload.LastSequence
        $queueDepth = [int]$response.Payload.QueueDepth
        if ($lastSequence -lt 0 -or $queueDepth -lt 0) { throw 'The Agent returned a negative quiet-witness counter.' }
        [pscustomobject]@{
            State = [string]$response.Payload.State
            LastSequence = $lastSequence
            QueueDepth = $queueDepth
            PendingReconciliationCount = @($response.Payload.PendingReconciliations).Count
        }
    }
    finally {
        if ($pipe -is [IDisposable]) { $pipe.Dispose() }
    }
}

function Get-ProcessPresence {
    $present = New-Object System.Collections.Generic.List[object]
    foreach ($name in @($ForbiddenProcessName | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $normalized = [IO.Path]::GetFileNameWithoutExtension($name)
        foreach ($process in @(Get-Process -Name $normalized -ErrorAction SilentlyContinue)) {
            if ($process.Id -eq $witnessProcessId) { continue }
            [void]$present.Add([pscustomobject]@{ Name = $process.ProcessName; Id = $process.Id })
            $process.Dispose()
        }
    }
    return @($present)
}

$startedUtc = [DateTimeOffset]::UtcNow
$samples = New-Object System.Collections.Generic.List[object]
$errors = New-Object System.Collections.Generic.List[string]
$initial = $null
$lastSequence = $null
$reconciliationActiveSamples = 0
$queueOverrunCount = 0
$bulkEventCount = $null
$completedUtc = $null
try {
    $initial = Get-AgentHealth
    $lastSequence = $initial.LastSequence
    $deadline = $startedUtc.AddSeconds($DurationSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $recordedUtc = [DateTimeOffset]::UtcNow
        try {
            $health = Get-AgentHealth
            if ($health.LastSequence -lt $lastSequence) { throw 'Agent LastSequence regressed during quiet witness measurement.' }
            $lastSequence = $health.LastSequence
            $processes = @(Get-ProcessPresence)
            if ($health.QueueDepth -gt $MaxQueueDepth) { $queueOverrunCount++ }
            if ($health.PendingReconciliationCount -gt 0) { $reconciliationActiveSamples++ }
            $samples.Add([pscustomobject]@{
                RecordedUtc = $recordedUtc
                LastSequence = $health.LastSequence
                QueueDepth = $health.QueueDepth
                PendingReconciliationCount = $health.PendingReconciliationCount
                ForbiddenProcesses = $processes
            })
        }
        catch {
            $errors.Add("${recordedUtc}: $($_.Exception.Message)")
        }
        Start-Sleep -Milliseconds $IntervalMilliseconds
    }
    $completedUtc = [DateTimeOffset]::UtcNow
    $final = Get-AgentHealth
    if ($final.LastSequence -lt $lastSequence) { throw 'Agent LastSequence regressed at quiet witness completion.' }
    $lastSequence = $final.LastSequence
    $bulkEventCount = $lastSequence - $initial.LastSequence
}
catch {
    $errors.Add("fatal: $($_.Exception.Message)")
    $completedUtc = [DateTimeOffset]::UtcNow
}

$allForbiddenProcesses = @($samples | ForEach-Object { @($_.ForbiddenProcesses) })
$sampleSpanSeconds = if ($samples.Count -ge 2) {
    ([DateTimeOffset]::Parse($samples[$samples.Count - 1].RecordedUtc) - [DateTimeOffset]::Parse($samples[0].RecordedUtc)).TotalSeconds
}
else { 0 }
$measuredSeconds = if ($null -ne $completedUtc) { ($completedUtc - $startedUtc).TotalSeconds } else { 0 }
$reconciliationActiveSeconds = [Math]::Round($reconciliationActiveSamples * ($IntervalMilliseconds / 1000.0), 3)
$initialLastSequence = if ($null -ne $initial) { [int64]$initial.LastSequence } else { $null }
$eligible = $errors.Count -eq 0 -and
    $null -ne $initial -and
    $null -ne $bulkEventCount -and
    $measuredSeconds -ge ($DurationSeconds - ([Math]::Max(5, 2 * $IntervalMilliseconds / 1000.0))) -and
    $samples.Count -ge [Math]::Max(2, [Math]::Floor(($DurationSeconds * 1000) / $IntervalMilliseconds * 0.8)) -and
    $bulkEventCount -eq 0 -and
    $queueOverrunCount -eq 0 -and
    $allForbiddenProcesses.Count -eq 0
$witnessStatus = if ($eligible) { 'PASSED' } else { 'FAILED' }

$witness = [ordered]::new()
$witness['Schema'] = 'StorageChronicle.ResourceQuietWitness.v1'
$witness['Status'] = $witnessStatus
$witness['AcceptanceEligible'] = [bool]$eligible
$witness['QuietPeriodStartedUtc'] = $startedUtc
$witness['QuietPeriodCompletedUtc'] = $completedUtc
$witness['QuietPeriodSeconds'] = [Math]::Round($measuredSeconds, 3)
$witness['BulkEventCount'] = $bulkEventCount
$witness['BulkEventCountSource'] = 'AgentHealth.LastSequenceDelta'
$witness['QueueOverrunCount'] = $queueOverrunCount
$witness['QueueOverrunSource'] = 'Observed AgentHealth.QueueDepth above configured bound'
$witness['ReconciliationActiveTimeSeconds'] = $reconciliationActiveSeconds
$witness['ReconciliationActiveSource'] = 'Observed non-empty AgentHealth.PendingReconciliations samples'
$witness['BenchmarkWorkloadProcessesAbsent'] = $allForbiddenProcesses.Count -eq 0
$witness['BuildTestProcessesAbsent'] = $allForbiddenProcesses.Count -eq 0
$witness['ForbiddenProcessMatches'] = @($allForbiddenProcesses)
$witness['AgentPipeName'] = $AgentPipeName
$witness['InitialLastSequence'] = $initialLastSequence
$witness['FinalLastSequence'] = $lastSequence
$witness['SampleCount'] = $samples.Count
$witness['SampleSpanSeconds'] = [Math]::Round($sampleSpanSeconds, 3)
$witness['Samples'] = $samples.ToArray()
$witness['Errors'] = $errors.ToArray()
$witness['GeneratedUtc'] = [DateTimeOffset]::UtcNow
$witness | ConvertTo-Json -Depth 12 | Set-Content -Encoding UTF8 -LiteralPath $fullOutputPath
if (-not $eligible) {
    Write-Error "Quiet witness is not acceptance-eligible: $fullOutputPath"
    exit 1
}
Write-Output "Quiet witness completed: $fullOutputPath"
exit 0
