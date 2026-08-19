param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) { Write-Host 'Windows privileged tests are skipped on a non-Windows host.'; exit 0 }
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifact = Join-Path $root 'artifacts/quality/privileged'
New-Item -ItemType Directory -Force -Path $artifact | Out-Null
$projects = Get-ChildItem (Join-Path $root 'tests') -Recurse -Filter '*Windows*.Tests.csproj' | Sort-Object FullName
if ($projects.Count -eq 0) { Write-Error 'No Windows privileged test projects were found.' }
foreach ($project in $projects) {
    $assembly = [IO.Path]::GetFileNameWithoutExtension($project.FullName)
    $exe = Get-ChildItem (Join-Path $project.DirectoryName 'bin') -Recurse -Filter "$assembly.exe" | Where-Object { $_.FullName -match "\\$Configuration\\" } | Select-Object -First 1
    if ($null -eq $exe) { Write-Error "Test executable not found: $assembly.exe" }
    & $exe.FullName --progress off --minimum-expected-tests 1 --filter-trait 'Category=WindowsPrivileged' --report-junit --report-junit-filename (Join-Path $artifact "$assembly.xml")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
exit 0
