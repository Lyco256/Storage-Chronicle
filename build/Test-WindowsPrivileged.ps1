[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$AcceptanceRoot,
    [string]$TestId,
    [string]$TestLabRoot,
    [string]$VhdxPath,
    [string]$VhdxRoot,
    [string]$DevicePath,
    [string]$RemovableRoot,
    [string]$SmbShareName,
    [string]$ServiceName = 'StorageChronicleAgent',
    [string]$SessionAgentExecutable,
    [string]$WorkloadOraclePath,
    [string]$AgentHistoryPath,
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [switch]$CreateVhdx,
    [switch]$ConfirmCreateVhdx,
    [switch]$CreateUsnJournal,
    [switch]$WaitForMediaChange,
    [int]$MediaTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$arguments = @{
    Configuration = $Configuration
    AcceptanceRoot = $AcceptanceRoot
    TestId = $TestId
    VhdxPath = $VhdxPath
    VhdxRoot = $VhdxRoot
    TestLabRoot = $TestLabRoot
    DevicePath = $DevicePath
    RemovableRoot = $RemovableRoot
    SmbShareName = $SmbShareName
    ServiceName = $ServiceName
    SessionAgentExecutable = $SessionAgentExecutable
    WorkloadOraclePath = $WorkloadOraclePath
    AgentHistoryPath = $AgentHistoryPath
    EvidenceRoot = $EvidenceRoot
    CreateVhdx = $CreateVhdx
    ConfirmCreateVhdx = $ConfirmCreateVhdx
    CreateUsnJournal = $CreateUsnJournal
    WaitForMediaChange = $WaitForMediaChange
    MediaTimeoutSeconds = $MediaTimeoutSeconds
}
& (Join-Path $PSScriptRoot 'Test-Privileged.ps1') @arguments
exit $LASTEXITCODE
