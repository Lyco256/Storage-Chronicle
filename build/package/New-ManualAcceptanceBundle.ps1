[CmdletBinding()]
param(
    [ValidateSet('Windows10-22H2', 'Windows11', 'Both')][string]$Target = 'Both',
    [string]$MsiPath,
    [string]$UpdatedMsiPath,
    [string]$RollbackMsiPath,
    [string]$OutputRoot,
    [switch]$Build,
    [switch]$AllowIncompleteBundle,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $root 'artifacts/manual' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

function Resolve-OptionalPath {
    param([AllowNull()][string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $full = if ([IO.Path]::IsPathRooted($Value)) { $Value } else { Join-Path $root $Value }
    return [IO.Path]::GetFullPath($full)
}

if ($Build) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-Installer.ps1')
    if ($LASTEXITCODE -ne 0) { throw "Build-Installer.ps1 failed with exit code $LASTEXITCODE." }
}

$MsiPath = Resolve-OptionalPath $MsiPath
if ([string]::IsNullOrWhiteSpace($MsiPath)) {
    $candidates = @(
        (Join-Path $root 'installer/bin/x64/Release/StorageChronicle.msi'),
        (Join-Path $root 'artifacts/installer/StorageChronicle.msi')
    )
    $MsiPath = @($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
}
$UpdatedMsiPath = Resolve-OptionalPath $UpdatedMsiPath
$RollbackMsiPath = Resolve-OptionalPath $RollbackMsiPath
$missing = [System.Collections.Generic.List[string]]::new()
if ([string]::IsNullOrWhiteSpace($MsiPath) -or -not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) { [void]$missing.Add('StorageChronicle.msi') }
if ([string]::IsNullOrWhiteSpace($UpdatedMsiPath) -or -not (Test-Path -LiteralPath $UpdatedMsiPath -PathType Leaf)) { [void]$missing.Add('StorageChronicle.updated.msi') }
if ([string]::IsNullOrWhiteSpace($RollbackMsiPath) -or -not (Test-Path -LiteralPath $RollbackMsiPath -PathType Leaf)) { [void]$missing.Add('StorageChronicle.rollback.msi') }

$publishRoots = [ordered]@{
    agent = Join-Path $root 'artifacts/installer/agent'
    'session-agent' = Join-Path $root 'artifacts/installer/session-agent'
    ui = Join-Path $root 'artifacts/installer/ui'
}
foreach ($entry in $publishRoots.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Value -PathType Container)) { [void]$missing.Add("self-contained/$($entry.Key)") }
}
if (-not $AllowIncompleteBundle -and $missing.Count -gt 0) { throw ('Bundle inputs are missing: ' + ($missing -join ', ') + '. Supply all MSI versions and run Build-Installer.ps1 first, or use -AllowIncompleteBundle for a non-eligible preparation bundle.') }

$targets = if ($Target -eq 'Both') { @('Windows10-22H2', 'Windows11') } else { @($Target) }
$commonScripts = @(
    (Join-Path $root 'build/package/Test-Installer.ps1'),
    (Join-Path $root 'tools/PhysicalAcceptance/Invoke-RealInstallerCase.ps1'),
    (Join-Path $root 'tools/PhysicalAcceptance/Probe-WriteAccess.ps1'),
    (Join-Path $root 'tools/PhysicalAcceptance/Run-RealMachineInstallerAcceptance.ps1'),
    (Join-Path $root 'tools/PhysicalAcceptance/Cleanup-RealMachineInstallerAcceptance.ps1'),
    (Join-Path $root 'tools/PhysicalAcceptance/Collect-PhysicalAcceptanceResults.ps1')
)
foreach ($scriptPath in $commonScripts) { if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw "Bundle source script is missing: $scriptPath" } }
$win10Verifier = Join-Path $root 'tools/PhysicalAcceptance/Verify-Windows10PhysicalAcceptance.ps1'
if (-not (Test-Path -LiteralPath $win10Verifier -PathType Leaf)) { throw "Windows 10 verifier is missing: $win10Verifier" }

function Copy-Payload {
    param([string]$Source, [string]$Destination)
    if (-not (Test-Path -LiteralPath $Source)) { return $false }
    if ((Test-Path -LiteralPath $Destination) -and -not $Force) { throw "Destination already exists; use -Force only for an approved generated-artifact directory: $Destination" }
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force:$Force
    return $true
}

function New-Bundle {
    param([Parameter(Mandatory = $true)][string]$TargetOs)
    $name = if ($TargetOs -eq 'Windows10-22H2') { 'windows10-physical-acceptance' } else { 'installer-acceptance' }
    $bundle = Join-Path $OutputRoot $name
    if ((Test-Path -LiteralPath $bundle) -and -not $Force) { throw "Bundle directory already exists; use -Force for generated artifacts only: $bundle" }
    New-Item -ItemType Directory -Force -Path $bundle | Out-Null

    $payloadFiles = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($MsiPath) -and (Test-Path -LiteralPath $MsiPath -PathType Leaf)) {
        Copy-Item -LiteralPath $MsiPath -Destination (Join-Path $bundle 'StorageChronicle.msi') -Force:$Force
        [void]$payloadFiles.Add('StorageChronicle.msi')
    }
    if (-not [string]::IsNullOrWhiteSpace($UpdatedMsiPath) -and (Test-Path -LiteralPath $UpdatedMsiPath -PathType Leaf)) {
        Copy-Item -LiteralPath $UpdatedMsiPath -Destination (Join-Path $bundle 'StorageChronicle.updated.msi') -Force:$Force
        [void]$payloadFiles.Add('StorageChronicle.updated.msi')
    }
    if (-not [string]::IsNullOrWhiteSpace($RollbackMsiPath) -and (Test-Path -LiteralPath $RollbackMsiPath -PathType Leaf)) {
        Copy-Item -LiteralPath $RollbackMsiPath -Destination (Join-Path $bundle 'StorageChronicle.rollback.msi') -Force:$Force
        [void]$payloadFiles.Add('StorageChronicle.rollback.msi')
    }
    foreach ($entry in $publishRoots.GetEnumerator()) {
        if (Test-Path -LiteralPath $entry.Value -PathType Container) {
            $destination = Join-Path $bundle $entry.Key
            Copy-Item -LiteralPath $entry.Value -Destination $destination -Recurse -Force:$Force
            foreach ($file in @(Get-ChildItem -LiteralPath $destination -File -Recurse)) { [void]$payloadFiles.Add(($file.FullName.Substring($bundle.Length).TrimStart('\').Replace('\', '/'))) }
        }
    }
    foreach ($scriptPath in $commonScripts) {
        $destination = Join-Path $bundle ([IO.Path]::GetFileName($scriptPath))
        Copy-Item -LiteralPath $scriptPath -Destination $destination -Force:$Force
        [void]$payloadFiles.Add([IO.Path]::GetFileName($scriptPath))
    }
    if ($TargetOs -eq 'Windows10-22H2') {
        Copy-Item -LiteralPath $win10Verifier -Destination (Join-Path $bundle 'Verify-Windows10PhysicalAcceptance.ps1') -Force:$Force
        [void]$payloadFiles.Add('Verify-Windows10PhysicalAcceptance.ps1')
    }

    $hashEntries = [System.Collections.Generic.List[object]]::new()
    foreach ($relative in @($payloadFiles | Select-Object -Unique)) {
        $path = Join-Path $bundle $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $path
            [void]$hashEntries.Add([ordered]@{ RelativePath = $relative.Replace('\', '/'); SHA256 = $hash.Hash; Length = (Get-Item -LiteralPath $path).Length })
        }
    }
    $hashManifest = [ordered]@{ Schema = 'StorageChronicle.ManualAcceptanceHashManifest.v1'; GeneratedUtc = [DateTimeOffset]::UtcNow; Files = @($hashEntries) }
    $hashManifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $bundle 'hash-manifest.json') -Encoding UTF8

    $bundleManifest = [ordered]@{
        Schema = 'StorageChronicle.ManualAcceptanceBundle.v1'
        GeneratedUtc = [DateTimeOffset]::UtcNow
        TargetOs = $TargetOs
        ProductName = 'Storage Chronicle'
        ExecutionStatus = 'prepared'
        AcceptanceEligible = $false
        MissingInputs = @($missing)
        PayloadFiles = @($hashEntries | ForEach-Object RelativePath)
        Instructions = @('Run as administrator on a disposable target.', 'Verify the target OS and test-data marker before confirmation.', 'Use a real non-admin credential reference; never put plaintext passwords in the bundle.', 'Collect results and keep NOT_EXECUTED/FAILED cases visible.')
    }
    $bundleManifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $bundle 'bundle-manifest.json') -Encoding UTF8
    $readme = @"
# Storage Chronicle manual acceptance bundle

Target: `$TargetOs`

This bundle is preparation evidence only. `AcceptanceEligible` is `false` until the real target run produces a complete, passing manifest. It must be executed on a disposable physical target with an approved test-data root; VM or fixture results do not close the physical gate.

## Run

1. Prepare a dedicated test-data VHDX/root and ensure it contains `.storage-chronicle-testlab-marker.json` (the marker contract created by the TestLab data-volume preparation step).
2. Create a DPAPI-protected credential reference on the target, for example `Get-Credential | Export-Clixml .\nonadmin-credential.xml`.
3. From an elevated PowerShell run:

`.Run-RealMachineInstallerAcceptance.ps1 -TestDataRoot 'D:\SC-Acceptance' -NonAdminCredentialReference '.\nonadmin-credential.xml' -NonAdminUser 'MACHINE\StandardUser' -SessionUser 'MACHINE\StandardUser'`

The script verifies the target OS, x64, administrator token, free space, bundle hashes, and marker before asking for an exact `YES` confirmation. It does not claim success from an exit code alone.

For Windows 10, run `.Verify-Windows10PhysicalAcceptance.ps1` first. Collect only the generated result files with `.Collect-PhysicalAcceptanceResults.ps1 -BundleRoot .`.

Cleanup requires explicit `-ConfirmCleanup`; it never removes `%ProgramData%\Storage Chronicle\history`.
"@
    Set-Content -LiteralPath (Join-Path $bundle 'README.md') -Value $readme -Encoding UTF8
    Write-Output ([ordered]@{ Bundle = $bundle; TargetOs = $TargetOs; MissingInputs = @($missing); AcceptanceEligible = $false } | ConvertTo-Json -Depth 8)
}

foreach ($targetOs in $targets) { New-Bundle -TargetOs $targetOs }
exit 0
