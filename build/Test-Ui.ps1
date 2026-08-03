$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projects = Get-ChildItem (Join-Path $root 'tests') -Recurse -Filter '*Ui*.Tests.csproj' -ErrorAction SilentlyContinue
foreach ($project in $projects) {
    dotnet build $project.FullName --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $assembly = Get-ChildItem (Join-Path $project.Directory.FullName 'bin\Debug') -Recurse -File -Filter ($project.BaseName + '.dll') |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $assembly) { Write-Error "Test assembly was not produced: $($project.BaseName)"; exit 6 }
    dotnet test $assembly.FullName.Substring($root.Length + 1) --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
