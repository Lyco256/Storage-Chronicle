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

    $assembly = Get-ChildItem (Join-Path $project.Directory.FullName 'bin\Debug') -Recurse -File -Filter ($project.BaseName + '.dll') |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $assembly) {
        Write-Error "Test assembly was not produced: $($project.BaseName)"
        exit 6
    }

    # .NET 10's MTP project invocation can select the legacy VSTest target for a single project.
    # The emitted MTP test module is the authoritative executable boundary and avoids a false 0-test result.
    $assemblyArgument = $assembly.FullName.Substring($root.Length + 1)
    $args = @('test', $assemblyArgument)
    if ($NoRestore) { $args += '--no-restore' }
    $log = Join-Path $logRoot ($project.BaseName + '.log')
    Write-Host "Running $($project.BaseName)"
    & dotnet @args 2>&1 | Tee-Object -FilePath $log
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        Write-Error "Test project failed: $($project.BaseName) (exit code $exitCode)"
        exit $exitCode
    }
}
exit 0
