[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts/aot-compatibility'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $root $OutputDirectory
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$projects = @(
    @{ Name = 'Agent'; Path = 'src/StorageChronicle.Agent/StorageChronicle.Agent.csproj' },
    @{ Name = 'SessionAgent'; Path = 'src/StorageChronicle.SessionAgent/StorageChronicle.SessionAgent.csproj' }
)
$results = [System.Collections.Generic.List[object]]::new()
foreach ($project in $projects) {
    $output = Join-Path $OutputDirectory $project.Name
    $arguments = @(
        'publish',
        (Join-Path $root $project.Path),
        '--configuration', $Configuration,
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--output', $output,
        '-p:StorageChronicleAotCompatibility=true'
    )
    & dotnet @arguments
    $results.Add([pscustomobject]@{
        Project = $project.Name
        ExitCode = $LASTEXITCODE
        OutputDirectory = $output
    })
    if ($LASTEXITCODE -ne 0) {
        $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'aot-compatibility.json') -Encoding UTF8
        exit $LASTEXITCODE
    }
}

$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'aot-compatibility.json') -Encoding UTF8
Write-Output 'Native AOT compatibility publication completed for Agent and SessionAgent.'
