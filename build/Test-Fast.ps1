param([switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projects = Get-ChildItem (Join-Path $root 'tests') -Recurse -Filter '*.Tests.csproj' |
    Where-Object { $_.FullName -notmatch 'StorageChronicle\.Platform\.Windows\.Integration\.Tests\.csproj$' } |
    Sort-Object FullName
$logRoot = Join-Path $root 'artifacts/test-fast'
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
foreach ($project in $projects) {
    $buildArgs = @('build', $project.FullName, '--nologo')
    if ($NoRestore) { $buildArgs += '--no-restore' }
    Write-Host "Building $($project.BaseName)"
    & dotnet @buildArgs 2>&1 | Tee-Object -FilePath (Join-Path $logRoot ($project.BaseName + '.build.log'))
    $buildExitCode = $LASTEXITCODE
    if ($buildExitCode -ne 0) {
        Write-Error "Build failed: $($project.BaseName) (exit code $buildExitCode)"
        exit $buildExitCode
    }

    $testExecutable = Get-ChildItem (Join-Path $project.Directory.FullName 'bin\Debug') -Recurse -File -Filter ($project.BaseName + '.exe') |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $testExecutable) {
        Write-Error "MTP test executable was not produced: $($project.BaseName)"
        exit 6
    }

    $log = Join-Path $logRoot ($project.BaseName + '.log')
    Write-Host "Running $($project.BaseName)"
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Some tests intentionally write their expected diagnostics to stderr; capture it without turning a passing process into a PowerShell error.
        $ErrorActionPreference = 'Continue'
        & $testExecutable.FullName --progress off --minimum-expected-tests 1 2>&1 | Tee-Object -FilePath $log
    }
    finally { $ErrorActionPreference = $previousErrorActionPreference }
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        Write-Error "Test project failed: $($project.BaseName) (exit code $exitCode)"
        exit $exitCode
    }
}
exit 0
