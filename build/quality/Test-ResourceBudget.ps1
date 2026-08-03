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
    [switch]$Diagnostic
)

$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The resource-budget acceptance measurement requires Windows because PrivateWorkingSetSize, process I/O counters, and the Agent named pipe are Windows acceptance surfaces.'
}

if ($DurationSeconds -ne 600 -and -not $Diagnostic) {
    throw 'Acceptance measurements must run for exactly 600 seconds. Use -Diagnostic explicitly for a shorter non-acceptance measurement.'
}

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$monitorProject = Join-Path $root 'tools/StorageChronicle.ResourceMonitor/StorageChronicle.ResourceMonitor.csproj'
$artifactDirectory = Join-Path $root 'artifacts/quality/resources'
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

$processIds = @($ProcessId -split '[,;]' | ForEach-Object {
    $parsed = 0
    if (-not [int]::TryParse($_.Trim(), [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed) -or $parsed -le 0) {
        throw "Invalid process ID: $_"
    }

    try { [Diagnostics.Process]::GetProcessById($parsed).Dispose() } catch { throw "Process ID $parsed is not running or cannot be opened: $($_.Exception.Message)" }
    $parsed
}) | Sort-Object -Unique

if ($processIds.Count -eq 0) { throw 'At least one process ID is required.' }
if ([string]::IsNullOrWhiteSpace($AgentPipeName)) { throw 'AgentPipeName must not be empty.' }

function Read-ExactBytes {
    param(
        [Parameter(Mandatory = $true)][System.IO.Stream]$Stream,
        [Parameter(Mandatory = $true)][byte[]]$Buffer
    )

    $offset = 0
    while ($offset -lt $Buffer.Length) {
        $read = $Stream.Read($Buffer, $offset, $Buffer.Length - $offset)
        if ($read -le 0) { throw 'The Agent health named pipe closed before the response frame was complete.' }
        $offset += $read
    }
}

function Get-AgentQueueDepth {
    $pipe = $null
    try {
        $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $AgentPipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::None)
        $pipe.Connect(2000)
        $pipe.ReadTimeout = 2000
        $pipe.WriteTimeout = 2000

        $requestJson = '{"Protocol":{"Major":1,"Minor":0},"MessageType":"AgentHealthRequest","Payload":{}}'
        $payload = [Text.Encoding]::UTF8.GetBytes($requestJson)
        $frame = [byte[]]::new(4 + $payload.Length)
        [BitConverter]::GetBytes([int32]$payload.Length).CopyTo($frame, 0)
        [Array]::Copy($payload, 0, $frame, 4, $payload.Length)
        $pipe.Write($frame, 0, $frame.Length)
        $pipe.Flush()

        $header = [byte[]]::new(4)
        Read-ExactBytes -Stream $pipe -Buffer $header
        $length = [BitConverter]::ToInt32($header, 0)
        if ($length -le 0 -or $length -gt 8MB) { throw "The Agent health response frame length $length is outside the IPC limit." }
        $responseBytes = [byte[]]::new($length)
        Read-ExactBytes -Stream $pipe -Buffer $responseBytes
        $response = [Text.Encoding]::UTF8.GetString($responseBytes) | ConvertFrom-Json
        if ($response.MessageType -ne 'AgentHealth') { throw "The Agent health pipe returned message type '$($response.MessageType)' instead of AgentHealth." }
        $depth = [int]$response.Payload.QueueDepth
        if ($depth -lt 0) { throw 'The Agent returned a negative queue depth.' }
        return $depth
    }
    finally {
        if ($pipe -is [IDisposable]) { $pipe.Dispose() }
    }
}

$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ', [Globalization.CultureInfo]::InvariantCulture)
$output = Join-Path $artifactDirectory ("process-" + ($processIds -join '-') + '-' + $stamp + '.json')
$monitorArguments = @(
    'run', '--project', $monitorProject, '-c', $Configuration, '--no-restore', '--',
    '--duration-seconds', $DurationSeconds,
    '--interval-ms', $IntervalMilliseconds,
    '--output', $output,
    '--max-private-mib', $MaxPrivateMiB.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--max-cpu-percent', $MaxCpuPercent.ToString([Globalization.CultureInfo]::InvariantCulture)
)
foreach ($processValue in $processIds) { $monitorArguments += @('--pid', $processValue) }

$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = 'dotnet'
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$argumentListProperty = [Diagnostics.ProcessStartInfo].GetProperty('ArgumentList')
if ($null -ne $argumentListProperty -and $null -ne $startInfo.ArgumentList) {
    foreach ($argument in $monitorArguments) { [void]$startInfo.ArgumentList.Add([string]$argument) }
}
else {
    $quotedArguments = foreach ($argument in $monitorArguments) {
        $text = [string]$argument
        if ($text -notmatch '[\s"]') { $text; continue }
        $escaped = $text -replace '(\\*)"', '$1$1\\"'
        $escaped = $escaped -replace '(\\+)$', '$1$1'
        '"' + $escaped + '"'
    }
    $startInfo.Arguments = $quotedArguments -join ' '
}

$monitor = [Diagnostics.Process]::new()
$monitor.StartInfo = $startInfo
$queueSamples = [System.Collections.Generic.List[object]]::new()
$queueErrors = [System.Collections.Generic.List[string]]::new()
$startedUtc = [DateTimeOffset]::UtcNow
$monitorExitCode = -1

try {
    if (-not $monitor.Start()) { throw 'The resource monitor process could not be started.' }
    while (-not $monitor.HasExited) {
        $sampleUtc = [DateTimeOffset]::UtcNow
        try {
            $depth = Get-AgentQueueDepth
            $queueSamples.Add([pscustomobject]@{ RecordedUtc = $sampleUtc; QueueDepth = $depth })
        }
        catch {
            $queueErrors.Add($_.Exception.Message)
        }
        Start-Sleep -Milliseconds $IntervalMilliseconds
    }

    $monitor.WaitForExit()
    $monitorExitCode = $monitor.ExitCode
    $monitorOutput = $monitor.StandardOutput.ReadToEnd()
    $monitorError = $monitor.StandardError.ReadToEnd()
    if (-not (Test-Path -LiteralPath $output)) {
        throw "The resource monitor did not produce its JSON result. ExitCode=$monitorExitCode; stderr=$monitorError; stdout=$monitorOutput"
    }

    $resource = Get-Content -Raw -Encoding UTF8 -LiteralPath $output | ConvertFrom-Json
    $queueValues = @($queueSamples | ForEach-Object { [int]$_.QueueDepth })
    $expectedSamples = [Math]::Max(2, [Math]::Floor(($DurationSeconds * 1000) / $IntervalMilliseconds))
    $minimumSamples = [Math]::Max(2, [Math]::Floor($expectedSamples * 0.8))
    $queueSamplingComplete = $queueValues.Count -ge $minimumSamples -and $queueErrors.Count -eq 0
    $queueMaximum = if ($queueValues.Count -gt 0) { ($queueValues | Measure-Object -Maximum).Maximum } else { $null }
    $queueMinimum = if ($queueValues.Count -gt 0) { ($queueValues | Measure-Object -Minimum).Minimum } else { $null }
    $queueAverage = if ($queueValues.Count -gt 0) { ($queueValues | Measure-Object -Average).Average } else { $null }
    $acceptanceEligible = $DurationSeconds -eq 600 -and -not $Diagnostic -and $queueSamplingComplete
    $queueLimitExceeded = $null -ne $queueMaximum -and $queueMaximum -gt $MaxQueueDepth

    $queueReport = [ordered]@{
        PipeName = $AgentPipeName
        SampleCount = $queueValues.Count
        ExpectedMinimumSamples = $minimumSamples
        MissedSamples = $queueErrors.Count
        Minimum = $queueMinimum
        Maximum = $queueMaximum
        Average = $queueAverage
        MaxQueueDepth = $MaxQueueDepth
        QueueDepthLimitExceeded = $queueLimitExceeded
        SamplingComplete = $queueSamplingComplete
        Samples = @($queueSamples)
        Errors = @($queueErrors | Select-Object -First 10)
    }
    $resource | Add-Member -NotePropertyName QueueDepth -NotePropertyValue ([pscustomobject]$queueReport) -Force
    $resource | Add-Member -NotePropertyName AcceptanceEligible -NotePropertyValue $acceptanceEligible -Force
    $resource | Add-Member -NotePropertyName MeasurementBoundary -NotePropertyValue ([pscustomobject]@{
        OperatingSystem = [Environment]::OSVersion.VersionString
        DurationSeconds = $DurationSeconds
        Diagnostic = [bool]$Diagnostic
        ProcessIds = @($processIds)
        StartedUtc = $startedUtc
        CompletedUtc = [DateTimeOffset]::UtcNow
    }) -Force
    $resource | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath $output

    if ($monitorExitCode -ne 0 -or $resource.PrivateMemoryLimitExceeded -or $resource.CpuLimitExceeded -or $queueLimitExceeded -or -not $acceptanceEligible) {
        Write-Error "Resource budget acceptance failed or is ineligible. Result: $output"
        exit 1
    }

    Write-Output "Resource budget acceptance passed: $output"
    exit 0
}
finally {
    if ($monitor -is [IDisposable]) { $monitor.Dispose() }
}
