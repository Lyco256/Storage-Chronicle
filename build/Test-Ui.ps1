$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projects = Get-ChildItem (Join-Path $root 'tests') -Recurse -Filter '*Ui*.Tests.csproj' -ErrorAction SilentlyContinue
foreach ($project in $projects) { dotnet test $project.FullName; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
