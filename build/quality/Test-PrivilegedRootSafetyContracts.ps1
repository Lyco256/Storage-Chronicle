$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot 'AcceptanceContracts.ps1')

$passed = 0
$failed = [Collections.Generic.List[string]]::new()
function Assert-RootPolicy([string]$Name, [string]$Path, [bool]$Expected) {
    $actual = Test-AcceptancePathIsProtected -Path $Path
    if ($actual -ne $Expected) { throw "$Name expected protected=$Expected but got $actual for $Path" }
    Write-Output "PASS $Name"
    $script:passed++
}

try {
    $profileRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    if ([string]::IsNullOrWhiteSpace($profileRoot)) { throw 'The current user profile path is unavailable.' }
    Assert-RootPolicy 'profile root exact rejection' $profileRoot $true

    foreach ($protectedRoot in @(
        $profileRoot,
        [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonProgramFiles),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonProgramFilesX86))) {
        if ([string]::IsNullOrWhiteSpace($protectedRoot)) { continue }
        Assert-RootPolicy 'special-folder descendant rejection' (Join-Path $protectedRoot 'StorageChronicleContractFixture') $true
    }

    $profileParent = [IO.Directory]::GetParent([IO.Path]::TrimEndingDirectorySeparator($profileRoot)).FullName
    $profileName = [IO.Path]::GetFileName([IO.Path]::TrimEndingDirectorySeparator($profileRoot))
    Assert-RootPolicy 'profile-prefix sibling boundary' (Join-Path $profileParent ($profileName + '-StorageChronicleFixture')) $false

    $volumeRoot = [IO.Path]::GetPathRoot($profileRoot)
    $variables = @('OneDrive', 'OneDriveCommercial', 'OneDriveConsumer')
    $previous = @{}
    try {
        foreach ($variable in $variables) {
            $previous[$variable] = [Environment]::GetEnvironmentVariable($variable, 'Process')
            $syntheticRoot = Join-Path $volumeRoot ("StorageChronicle-$variable-Contract")
            [Environment]::SetEnvironmentVariable($variable, $syntheticRoot, 'Process')
            Assert-RootPolicy "$variable exact-root rejection" $syntheticRoot $true
            Assert-RootPolicy "$variable descendant rejection" (Join-Path $syntheticRoot 'Fixture') $true
            Assert-RootPolicy "$variable prefix-sibling boundary" ($syntheticRoot + '-sibling\Fixture') $false
        }
    }
    finally {
        foreach ($variable in $variables) { [Environment]::SetEnvironmentVariable($variable, $previous[$variable], 'Process') }
    }

    $runnerPath = Join-Path $repositoryRoot 'build/Test-Privileged.ps1'
    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($runnerPath, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw "Test-Privileged.ps1 has PowerShell parse errors: $($parseErrors -join '; ')" }
    $passed++
    Write-Output 'PASS privileged runner parses without execution'
}
catch {
    $failed.Add($_.Exception.Message)
}

if ($failed.Count -gt 0) {
    foreach ($failure in $failed) { Write-Error $failure }
    throw "PrivilegedRootSafetyContracts failed: $($failed.Count) failure(s), $passed passed."
}

Write-Output "PrivilegedRootSafetyContracts Passed=$passed Failed=0"
