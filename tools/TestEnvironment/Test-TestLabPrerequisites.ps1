[CmdletBinding()]
param(
    [string]$CandidateRoot,
    [string]$Windows11Iso,
    [string]$Windows10Iso,
    [string]$OutputPath,
    [double]$MinimumMemoryGiB = 6,
    [double]$MinimumFreeGiB = 40
)

$ErrorActionPreference = 'Stop'
Write-Error 'Retired under Requirement 37: the VirtualBox host preflight is not a current acceptance procedure. This entry point performs no host, VM, file, settings, or evidence I/O.'
exit 2
