param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [string]$Configuration = 'Debug',
    [int]$DurationSeconds = 10,
    [int]$IntervalMilliseconds = 250
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifact = Join-Path $root 'artifacts/quality/resources'
New-Item -ItemType Directory -Force -Path $artifact | Out-Null
$output = Join-Path $artifact "process-$ProcessId.json"
dotnet run --project (Join-Path $root 'tools/StorageChronicle.ResourceMonitor/StorageChronicle.ResourceMonitor.csproj') -c $Configuration --no-restore -- --pid $ProcessId --duration-seconds $DurationSeconds --interval-ms $IntervalMilliseconds --output $output
exit $LASTEXITCODE
