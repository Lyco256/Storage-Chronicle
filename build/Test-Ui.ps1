$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projects = Get-ChildItem (Join-Path $root 'tests') -Recurse -Filter '*Ui*.Tests.csproj' -ErrorAction SilentlyContinue
foreach ($project in $projects) {
    dotnet build $project.FullName --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $testExecutable = Get-ChildItem (Join-Path $project.Directory.FullName 'bin\Debug') -Recurse -File -Filter ($project.BaseName + '.exe') |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $testExecutable) { Write-Error "MTP test executable was not produced: $($project.BaseName)"; exit 6 }
    & $testExecutable.FullName --progress off --minimum-expected-tests 1
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
