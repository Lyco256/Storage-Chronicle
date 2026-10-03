[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$RunId,
    [ValidateSet('Windows11', 'Windows10', 'Both')][string]$Target = 'Both',
    [string]$GuestWorkloadExecutable,
    [string]$GuestAgentExecutable,
    [string]$GuestDataRoot,
    [ValidateSet('Workload', 'Mft', 'NonNtfs', 'AclDenied')][string]$DataRole = 'Workload',
    [ValidateRange(1, 1000000)][int]$WorkloadCount = 10000,
    [string]$ExplorerEvidencePath,
    [string]$CorrelationEvidencePath,
    [pscredential]$Credential,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Write-Error 'This VM/guest TestLab runner is retired by Requirement 37. It performs no host, VM, disk, file, settings, or workload operations. Use only the audited physical acceptance workflow after all safety gates pass.'
exit 2
