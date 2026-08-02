$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projects = Get-ChildItem (Join-Path $root 'tests') -Recurse -Filter '*WindowsIntegration.Tests.csproj' -ErrorAction SilentlyContinue
foreach ($project in $projects) { dotnet test $project.FullName --filter 'Category=WindowsPrivileged'; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
