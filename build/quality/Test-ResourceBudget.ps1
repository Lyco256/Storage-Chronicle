param(
    [Parameter(Mandatory = $true)][string]$ProcessId,
    [string]$Configuration = 'Debug',
    [int]$DurationSeconds = 10,
    [int]$IntervalMilliseconds = 250
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifact = Join-Path $root 'artifacts/quality/resources'
New-Item -ItemType Directory -Force -Path $artifact | Out-Null
$processIds = @($ProcessId -split '[,;]' | ForEach-Object {
    $parsed = 0
    if (-not [int]::TryParse($_.Trim(), [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed) -or $parsed -le 0) { throw "Invalid process ID: $_" }
    $parsed
})
if ($processIds.Count -eq 0) { throw 'At least one process ID is required.' }
$output = Join-Path $artifact ("process-" + ($processIds -join '-') + ".json")
$monitorArgs = @('--duration-seconds', $DurationSeconds, '--interval-ms', $IntervalMilliseconds, '--output', $output)
foreach ($processValue in $processIds) { $monitorArgs += @('--pid', $processValue) }
dotnet run --project (Join-Path $root 'tools/StorageChronicle.ResourceMonitor/StorageChronicle.ResourceMonitor.csproj') -c $Configuration --no-restore -- @monitorArgs
exit $LASTEXITCODE
