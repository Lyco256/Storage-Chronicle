[CmdletBinding()]
param(
    [string]$ConfigPath,
    [ValidateSet('Windows11', 'Windows10', 'Both')][string]$Target = 'Windows11',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Write-Error 'Retired under Requirement 37: whole-OS VirtualBox TestLab creation is not used. This entry point performs no host, VM, disk, filesystem, or evidence I/O, regardless of -Apply. Use only the separately audited lightweight fixture/file-backed VHDX workflow after all safety gates pass.'
exit 2
