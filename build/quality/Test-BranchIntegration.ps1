[CmdletBinding()]
param(
    [string]$ExpectedAcceptedSha,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root ('artifacts/acceptance/branch-integration/branch-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
}
$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

function Invoke-GitValue([string[]]$Arguments) {
    $output = @(& git -C $root @Arguments 2>$null)
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n").Trim() }
}

$failureReasons = [System.Collections.Generic.List[string]]::new()
try {
    $branchResult = Invoke-GitValue @('branch', '--show-current')
    $headResult = Invoke-GitValue @('rev-parse', 'HEAD')
    $statusResult = Invoke-GitValue @('status', '--porcelain=v1')
    $devenvRef = Invoke-GitValue @('rev-parse', '--verify', 'refs/remotes/origin/devenv')
    $mainRef = Invoke-GitValue @('rev-parse', '--verify', 'refs/remotes/origin/main')
    $remoteDevenvResult = Invoke-GitValue @('ls-remote', '--exit-code', '--heads', 'origin', 'refs/heads/devenv')
    $remoteMainResult = Invoke-GitValue @('ls-remote', '--exit-code', '--heads', 'origin', 'refs/heads/main')
    $ancestorResult = if ($devenvRef.ExitCode -eq 0 -and $mainRef.ExitCode -eq 0) {
        Invoke-GitValue @('merge-base', '--is-ancestor', 'refs/remotes/origin/devenv', 'refs/remotes/origin/main')
    } else {
        [pscustomobject]@{ ExitCode = 128; Output = '' }
    }

    $branch = $branchResult.Output
    $headSha = $headResult.Output
    $devenvSha = if ($devenvRef.ExitCode -eq 0) { $devenvRef.Output } else { $null }
    $mainSha = if ($mainRef.ExitCode -eq 0) { $mainRef.Output } else { $null }
    $remoteDevenv = $remoteDevenvResult.ExitCode -eq 0
    $remoteMain = $remoteMainResult.ExitCode -eq 0
    $worktreeClean = [string]::IsNullOrWhiteSpace($statusResult.Output)
    $expectedMatch = -not [string]::IsNullOrWhiteSpace($ExpectedAcceptedSha) -and
        [string]::Equals($headSha, $ExpectedAcceptedSha.Trim(), [StringComparison]::OrdinalIgnoreCase)
    $mainContainsDevenv = $ancestorResult.ExitCode -eq 0

    if ($branch -ne 'main') { [void]$failureReasons.Add("Current branch is '$branch', not main.") }
    if (-not $remoteDevenv) { [void]$failureReasons.Add('origin/devenv is not available.') }
    if (-not $remoteMain) { [void]$failureReasons.Add('origin/main is not available.') }
    if (-not $worktreeClean) { [void]$failureReasons.Add('The worktree is not clean.') }
    if (-not $expectedMatch) { [void]$failureReasons.Add('HEAD does not match ExpectedAcceptedSha.') }
    if ([string]::IsNullOrWhiteSpace($mainSha) -or -not [string]::Equals($headSha, $mainSha, [StringComparison]::OrdinalIgnoreCase)) { [void]$failureReasons.Add('HEAD does not equal origin/main.') }
    if (-not $mainContainsDevenv) { [void]$failureReasons.Add('origin/main does not contain origin/devenv.') }

    $evidence = [ordered]@{
        Schema = 'StorageChronicle.BranchIntegrationEvidence.v1'
        Status = if ($failureReasons.Count -eq 0) { 'PASSED' } else { 'NOT_EXECUTED' }
        AcceptanceEligible = $failureReasons.Count -eq 0
        CurrentBranch = $branch
        HeadSha = $headSha
        ExpectedAcceptedSha = if ([string]::IsNullOrWhiteSpace($ExpectedAcceptedSha)) { $null } else { $ExpectedAcceptedSha.Trim() }
        ExpectedAcceptedShaMatch = $expectedMatch
        RemoteDevenv = $remoteDevenv
        RemoteMain = $remoteMain
        WorktreeClean = $worktreeClean
        DevenvSha = $devenvSha
        MainSha = $mainSha
        MainContainsDevenv = $mainContainsDevenv
        FailureReasons = @($failureReasons)
        GeneratedUtc = [DateTimeOffset]::UtcNow
    }
    $evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
    Write-Output ($evidence | ConvertTo-Json -Depth 12)
    if ($failureReasons.Count -ne 0) { exit 2 }
    exit 0
}
catch {
    $evidence = [ordered]@{
        Schema = 'StorageChronicle.BranchIntegrationEvidence.v1'
        Status = 'FAILED'
        AcceptanceEligible = $false
        FailureReasons = @($_.Exception.Message)
        GeneratedUtc = [DateTimeOffset]::UtcNow
    }
    $evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
    Write-Error $_.Exception.Message
    exit 1
}
