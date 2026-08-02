$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projects = Get-ChildItem (Join-Path $root 'benchmarks') -Recurse -Filter '*.csproj' -ErrorAction SilentlyContinue
foreach ($project in $projects) { dotnet run --project $project.FullName -c Release; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
