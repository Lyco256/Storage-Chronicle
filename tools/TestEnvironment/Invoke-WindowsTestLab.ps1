[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$RunId = ([guid]::NewGuid().ToString('N')),
    [ValidateSet('Windows11', 'Windows10', 'Both')][string]$Target = 'Both',
    [Parameter(Mandatory = $true)][string]$GuestWorkloadExecutable,
    [string]$GuestAgentExecutable,
    [string]$GuestDataRoot = 'D:\StorageChronicleTestData',
    [ValidateSet('Workload', 'Mft', 'NonNtfs', 'AclDenied')][string]$DataRole = 'Workload',
    [ValidateRange(1, 1000000)][int]$WorkloadCount = 10000,
    [string]$ExplorerEvidencePath,
    [string]$CorrelationEvidencePath,
    [pscredential]$Credential,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'TestLab.Common.ps1')

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactDirectory = New-TestLabArtifactDirectory -RepositoryRoot $repositoryRoot -RunId $RunId
$manifestPath = Join-Path $artifactDirectory 'testlab-manifest.json'
$manifest = [ordered]@{
    Schema = 'StorageChronicle.WindowsTestLabExecution.v2'
    RunId = $RunId
    Apply = [bool]$Apply
    Target = $Target
    Status = 'NOT_EXECUTED'
    AcceptanceEligible = $false
    ExecutionMode = 'TestLab'
    Diagnostic = $false
    AgentHostMode = 'TestLab'
    StartedUtc = [DateTimeOffset]::UtcNow
    DataRole = $DataRole
    WorkloadCount = $WorkloadCount
    Stages = @()
    ArtifactDirectory = $artifactDirectory
}
$activeVhdx = [System.Collections.Generic.List[object]]::new()
$cleanupFailures = [System.Collections.Generic.List[string]]::new()
$windows11OracleHostPath = $null
$windows11HistoryHostRoot = $null

function Add-Stage([string]$Name, [string]$Status, [string]$Reason, $Evidence = $null) {
    $manifest.Stages += [ordered]@{ Name = $Name; Status = $Status; Reason = $Reason; Evidence = $Evidence }
}

function Quote-GuestLiteral([string]$Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}

function Invoke-GuestCommand {
    param([string]$VmName, [string]$Command)
    $arguments = @{ VmName = $VmName; Command = $Command; ConfigPath = $ConfigPath }
    if ($null -ne $Credential) { $arguments.Credential = $Credential }
    $output = @(& (Join-Path $PSScriptRoot 'Invoke-TestLabCommand.ps1') @arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "VirtualBox guestcontrol command failed on ${VmName}: $($output -join [Environment]::NewLine)" }
    return $output
}

function Copy-GuestArtifactDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$VmName,
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $sourceRoot = [IO.Path]::GetFullPath($SourceDirectory)
    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) { throw "Guest artifact directory does not exist: $sourceRoot" }
    $files = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File)
    if ($files.Count -eq 0) { throw "Guest artifact directory is empty: $sourceRoot" }
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($sourceRoot, $file.FullName)
        $destination = Join-Path $DestinationDirectory $relative
        $parent = Split-Path -Parent $destination
        Invoke-GuestCommand -VmName $VmName -Command "New-Item -ItemType Directory -Force -Path $(Quote-GuestLiteral $parent) | Out-Null" | Out-Null
        $copyArguments = @{ VmName = $VmName; SourcePath = $file.FullName; DestinationPath = $destination; ConfigPath = $ConfigPath }
        if ($null -ne $Credential) { $copyArguments.Credential = $Credential }
        & (Join-Path $PSScriptRoot 'Copy-TestArtifactsToVm.ps1') @copyArguments 2>&1 | Out-File -LiteralPath $LogPath -Append -Encoding UTF8
        if ($LASTEXITCODE -ne 0) { throw "Guest artifact transfer failed for $($file.FullName) with exit code $LASTEXITCODE." }
    }
}

function Get-GuestDataDefinition {
    switch ($DataRole) {
        'Mft' { return [ordered]@{ Label = 'SC_TEST_MFT_VOLUME'; FileSystem = 'NTFS'; Scenario = 'mft' } }
        'NonNtfs' { return [ordered]@{ Label = 'SC_TEST_NONNTFS_VOLUME'; FileSystem = 'exFAT'; Scenario = 'full' } }
        'AclDenied' { return [ordered]@{ Label = 'SC_TEST_VOLUME'; FileSystem = 'NTFS'; Scenario = 'acl-denied' } }
        default { return [ordered]@{ Label = 'SC_TEST_VOLUME'; FileSystem = 'NTFS'; Scenario = 'full' } }
    }
}

function Assert-GuestDataMarker {
    param([string]$VmName, [string]$Root)
    $definition = Get-GuestDataDefinition
    $rootLiteral = Quote-GuestLiteral $Root
    $runIdLiteral = Quote-GuestLiteral $RunId
    $roleLiteral = Quote-GuestLiteral $DataRole
    $labelLiteral = Quote-GuestLiteral ([string]$definition.Label)
    $fileSystemLiteral = Quote-GuestLiteral ([string]$definition.FileSystem)
    $command = @"
`$root = $rootLiteral
`$expectedTestId = $runIdLiteral
`$expectedRole = $roleLiteral
`$expectedLabel = $labelLiteral
`$expectedFileSystem = $fileSystemLiteral
foreach (`$name in @('.storage-chronicle-testlab-marker.json', 'StorageChronicleTestVolume.json')) {
    `$path = Join-Path `$root `$name
    if (-not (Test-Path -LiteralPath `$path -PathType Leaf)) { throw "Required TestLab marker is missing: `$path" }
    `$marker = Get-Content -Raw -Encoding UTF8 -LiteralPath `$path | ConvertFrom-Json
    if ([string]`$marker.Schema -ne 'StorageChronicle.TestLabDataMarker.v1' -or [string]`$marker.TestId -ne `$expectedTestId -or [string]`$marker.Role -ne `$expectedRole -or [string]`$marker.VolumeLabel -ne `$expectedLabel -or [string]`$marker.FileSystem -ne `$expectedFileSystem) { throw "TestLab marker validation failed: `$path" }
}
"@
    Invoke-GuestCommand -VmName $VmName -Command $command | Out-File -LiteralPath (Join-Path $artifactDirectory "$VmName-marker-verify.log") -Encoding UTF8
}

function Initialize-GuestDataVolume {
    param([string]$VmName, [string]$Root)
    $definition = Get-GuestDataDefinition
    $rootLiteral = Quote-GuestLiteral $Root
    $labelLiteral = Quote-GuestLiteral ([string]$definition.Label)
    $fileSystemLiteral = Quote-GuestLiteral ([string]$definition.FileSystem)
    $runIdLiteral = Quote-GuestLiteral $RunId
    $roleLiteral = Quote-GuestLiteral $DataRole
    $command = @"
`$ErrorActionPreference = 'Stop'
`$root = $rootLiteral
`$label = $labelLiteral
`$fileSystem = $fileSystemLiteral
`$runId = $runIdLiteral
`$role = $roleLiteral
`$disk = @(Get-Disk | Where-Object { `$_.PartitionStyle -eq 'RAW' -and -not `$_.IsBoot } | Sort-Object Size)
if (`$disk.Count -ne 1) { throw 'Exactly one uninitialized non-boot data disk is required.' }
Initialize-Disk -Number `$disk[0].Number -PartitionStyle GPT -Confirm:`$false
`$partition = New-Partition -DiskNumber `$disk[0].Number -UseMaximumSize -AssignDriveLetter
Format-Volume -Partition `$partition -FileSystem `$fileSystem -NewFileSystemLabel `$label -Confirm:`$false | Out-Null
New-Item -ItemType Directory -Force -Path `$root | Out-Null
`$marker = [ordered]@{ Schema = 'StorageChronicle.TestLabDataMarker.v1'; TestId = `$runId; Role = `$role; VolumeLabel = `$label; FileSystem = `$fileSystem; CreatedUtc = [DateTimeOffset]::UtcNow }
`$json = `$marker | ConvertTo-Json -Depth 8
Set-Content -LiteralPath (Join-Path `$root '.storage-chronicle-testlab-marker.json') -Value `$json -Encoding UTF8
Set-Content -LiteralPath (Join-Path `$root 'StorageChronicleTestVolume.json') -Value `$json -Encoding UTF8
"@
    Invoke-GuestCommand -VmName $VmName -Command $command | Out-File -LiteralPath (Join-Path $artifactDirectory "$VmName-volume-init.log") -Encoding UTF8
}

try {
    $config = Get-TestLabConfig -ConfigPath $ConfigPath
    $root = Assert-TestLabRoot -Root $config.Root
    $guests = if ($Target -eq 'Both') { @('Windows11', 'Windows10') } else { @($Target) }
    if ($guests -contains 'Windows11') {
        if ([string]::IsNullOrWhiteSpace([string]$config.Windows11Iso)) { throw 'Windows 11 ISO is required when the Windows11 target is selected.' }
        Assert-ExistingIso -Path $config.Windows11Iso -Label 'Windows 11 ISO'
    }
    if ($guests -contains 'Windows10') {
        if ([string]::IsNullOrWhiteSpace([string]$config.Windows10Iso)) { throw 'Windows 10 22H2 ISO is required when the Windows10 target is selected.' }
        Assert-ExistingIso -Path $config.Windows10Iso -Label 'Windows 10 22H2 ISO'
    }
    $workloadSource = [IO.Path]::GetFullPath($GuestWorkloadExecutable)
    if (-not (Test-Path -LiteralPath $workloadSource -PathType Leaf)) { throw "Guest workload executable does not exist: $GuestWorkloadExecutable" }
    $workloadSourceDirectory = Split-Path -Parent $workloadSource
    $agentSource = $null
    if (-not [string]::IsNullOrWhiteSpace($GuestAgentExecutable)) {
        $agentSource = [IO.Path]::GetFullPath($GuestAgentExecutable)
        if (-not (Test-Path -LiteralPath $agentSource -PathType Leaf)) { throw "Guest Agent executable does not exist: $GuestAgentExecutable" }
    }
    $agentSourceDirectory = if ($null -ne $agentSource) { Split-Path -Parent $agentSource } else { $null }

    if (-not $Apply) {
        Add-Stage 'preflight' 'READY_FOR_USER_APPLY' 'Configuration, local ISO paths, and workload artifact are valid; no VM or guest operation was run.'
        foreach ($guest in $guests) { Add-Stage $guest 'NOT_EXECUTED' 'Pass -Apply only after the user approves baseline restore, data VHDX creation, guest format, workload, and cleanup.' }
        Add-Stage 'explorer-correlation' 'NOT_EXECUTED' 'Real Explorer actions require an interactive human-assisted session and an independent artifact.'
        $manifest.Status = 'READY_FOR_USER_APPLY'
        Write-TestLabJson -Path $manifestPath -Value $manifest
        Write-Output ($manifest | ConvertTo-Json -Depth 12)
        exit 2
    }

    $preflight = Assert-VirtualBoxHostPrerequisites -Root $root -Windows11Iso ([string]$config.Windows11Iso) -Windows10Iso ([string]$config.Windows10Iso)
    Add-Stage 'preflight' 'PASSED' 'VirtualBox host capability, safe root, resource profile, and selected ISO paths were validated without host elevation.' $preflight
    if ($DataRole -eq 'Mft') { $mftResource = Assert-VirtualBoxResourceGate -Root $root -RequireMftSeed; Add-Stage 'mft-resource-gate' 'PASSED' 'The 1M MFT seed resource gate passed before any guest data disk mutation.' $mftResource }
    $guestWorkloadDestination = 'C:\StorageChronicleTest\StorageChronicle.FileMutationWorkload.exe'
    $guestWorkloadDirectory = Split-Path -Parent $guestWorkloadDestination
    $guestAgentDestination = 'C:\StorageChronicleTest\StorageChronicle.Agent.exe'
    $guestAgentDirectory = Split-Path -Parent $guestAgentDestination
    $validatorProject = Join-Path $repositoryRoot 'tools/StorageChronicle.RealIoOracleValidator/StorageChronicle.RealIoOracleValidator.csproj'
    if ($null -ne $agentSource -and -not (Test-Path -LiteralPath $validatorProject -PathType Leaf)) { throw "Real-I/O oracle validator project is missing: $validatorProject" }
    $definitionData = Get-GuestDataDefinition
    $manifest.Scenario = $definitionData.Scenario

    foreach ($guest in $guests) {
        $definition = Get-TestLabVmDefinition -Guest $guest
        $vm = Assert-ExactTestLabVm -Name $definition.Name
        Assert-TestLabVmDisks -Vm $vm -Root $root
        Assert-TestLabVmProfile -Name $definition.Name -Root $root | Out-Null
        $safeRunId = $RunId -replace '[^A-Za-z0-9_.-]', '-'
        $vhdxPath = Assert-PathUnderRoot -Root $root -Path (Join-Path $root "data\$($definition.Name)\$safeRunId-$DataRole.vdi")
        $guestArtifactDirectory = Join-Path $artifactDirectory $definition.Name
        New-Item -ItemType Directory -Force -Path $guestArtifactDirectory | Out-Null
        $oracleGuestPath = Join-Path $GuestDataRoot "$RunId-oracle.json"
        $oracleHostPath = Join-Path $guestArtifactDirectory 'workload-oracle.json'
        $guestCommand = "& $(Quote-GuestLiteral $guestWorkloadDestination) --root $(Quote-GuestLiteral $GuestDataRoot) --oracle $(Quote-GuestLiteral $oracleGuestPath) --scenario $(Quote-GuestLiteral $definitionData.Scenario) --count $WorkloadCount --run-id $(Quote-GuestLiteral $RunId)"
        $historyGuestRoot = "C:\StorageChronicleAcceptance\$safeRunId\history"
        $historyGuestZip = "C:\StorageChronicleAcceptance\$safeRunId\agent-history.zip"
        $historyHostZip = Join-Path $guestArtifactDirectory 'agent-history.zip'
        $historyHostRoot = Join-Path $guestArtifactDirectory 'agent-history'
        $realIoEvidencePath = Join-Path $guestArtifactDirectory 'real-io-evidence.json'
        if ($guest -eq 'Windows11') {
            $windows11OracleHostPath = $oracleHostPath
            $windows11HistoryHostRoot = $historyHostRoot
        }
        $agentGuestCommand = @"
`$ErrorActionPreference = 'Stop'
`$agent = $null
`$historyRoot = $(Quote-GuestLiteral $historyGuestRoot)
`$historyZip = $(Quote-GuestLiteral $historyGuestZip)
`$settingsPath = Join-Path `$env:ProgramData 'Storage Chronicle\config\machine-settings.json'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent `$settingsPath), `$historyRoot | Out-Null
`$settings = [ordered]@{ schemaVersion = 1; settings = [ordered]@{ monitoringPaths = @($(Quote-GuestLiteral $GuestDataRoot)); excludedPaths = @(); noiseFilter = 0; logStoragePath = `$historyRoot; mediaMirrors = @{}; flushIntervalSeconds = 1 } }
`$settings | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath `$settingsPath -Encoding UTF8
`$agent = Start-Process -FilePath $(Quote-GuestLiteral $guestAgentDestination) -ArgumentList '--testlab' -PassThru -RedirectStandardOutput (Join-Path `$historyRoot 'agent.stdout.log') -RedirectStandardError (Join-Path `$historyRoot 'agent.stderr.log')
try {
    Start-Sleep -Seconds 5
    if (`$agent.HasExited) { throw "The Agent exited before the workload started with code `$(`$agent.ExitCode)." }
    $( $guestCommand )
    Start-Sleep -Seconds 10
    if (`$agent.HasExited) { throw "The Agent exited during the workload with code `$(`$agent.ExitCode)." }
}
finally {
    if (`$null -ne `$agent -and -not `$agent.HasExited) { Stop-Process -Id `$agent.Id -Force -ErrorAction SilentlyContinue; `$agent.WaitForExit(10000) | Out-Null }
}
if (-not (Test-Path -LiteralPath $(Quote-GuestLiteral $oracleGuestPath) -PathType Leaf)) { throw 'The workload oracle was not produced.' }
if (-not (Test-Path -LiteralPath `$historyRoot -PathType Container)) { throw 'The Agent history directory was not produced.' }
if (Test-Path -LiteralPath `$historyZip) { Remove-Item -LiteralPath `$historyZip -Force }
Compress-Archive -Path (Join-Path `$historyRoot '*') -DestinationPath `$historyZip -CompressionLevel Optimal
"@
        $markerVerified = $false
        try {
            & (Join-Path $PSScriptRoot 'Reset-TestVm.ps1') -Name $definition.Name -SnapshotName 'SC-CLEAN-BASELINE' -ConfigPath $ConfigPath -Apply | Out-File -LiteralPath (Join-Path $guestArtifactDirectory 'reset.log') -Encoding UTF8
            if ($LASTEXITCODE -ne 0) { throw "Baseline reset failed for $($definition.Name)." }
            $newArgs = @{ VmName = $definition.Name; Role = $DataRole; TestId = $RunId; ConfigPath = $ConfigPath; Apply = $true }
            & (Join-Path $PSScriptRoot 'New-TestDataVhdx.ps1') @newArgs | Out-File -LiteralPath (Join-Path $guestArtifactDirectory 'new-vhdx.log') -Encoding UTF8
            if ($LASTEXITCODE -ne 0) { throw "Data VHDX creation failed for $($definition.Name)." }
            [void]$activeVhdx.Add([pscustomobject]@{ VmName = $definition.Name; Path = $vhdxPath })
            Set-VBoxVmProvisioningSettings -Name $definition.Name -Provisioning:$false
            Start-TestLabVm -Name $definition.Name -Root $root
            Add-Stage $definition.Name 'STARTED' 'VirtualBox baseline restored, disposable dynamic data disk attached, networking disconnected, and the approved VM started.' $vhdxPath
            Copy-GuestArtifactDirectory -VmName $definition.Name -SourceDirectory $workloadSourceDirectory -DestinationDirectory $guestWorkloadDirectory -LogPath (Join-Path $guestArtifactDirectory 'copy-workload.log')
            if ($null -ne $agentSource) {
                Copy-GuestArtifactDirectory -VmName $definition.Name -SourceDirectory $agentSourceDirectory -DestinationDirectory $guestAgentDirectory -LogPath (Join-Path $guestArtifactDirectory 'copy-agent.log')
            }
            Initialize-GuestDataVolume -VmName $definition.Name -Root $GuestDataRoot
            $executionCommand = if ($null -ne $agentSource) { $agentGuestCommand } else { $guestCommand }
            if ($null -ne $agentSource) { Invoke-GuestCommand -VmName $definition.Name -Command $executionCommand | Out-File -LiteralPath (Join-Path $guestArtifactDirectory 'agent-and-workload.log') -Encoding UTF8 }
            else { Invoke-GuestCommand -VmName $definition.Name -Command $executionCommand | Out-File -LiteralPath (Join-Path $guestArtifactDirectory 'workload.log') -Encoding UTF8 }
            Assert-GuestDataMarker -VmName $definition.Name -Root $GuestDataRoot
            $markerVerified = $true
            & (Join-Path $PSScriptRoot 'Copy-TestResultsFromVm.ps1') -VmName $definition.Name -SourcePath $oracleGuestPath -DestinationPath $oracleHostPath -Credential $Credential -ConfigPath $ConfigPath | Out-File -LiteralPath (Join-Path $guestArtifactDirectory 'copy-oracle.log') -Encoding UTF8
            if ($LASTEXITCODE -ne 0) { throw "Oracle retrieval failed for $($definition.Name)." }
            if ($null -ne $agentSource) {
                & (Join-Path $PSScriptRoot 'Copy-TestResultsFromVm.ps1') -VmName $definition.Name -SourcePath $historyGuestZip -DestinationPath $historyHostZip -Credential $Credential -ConfigPath $ConfigPath | Out-File -LiteralPath (Join-Path $guestArtifactDirectory 'copy-agent-history.log') -Encoding UTF8
                if ($LASTEXITCODE -ne 0) { throw "Agent history retrieval failed for $($definition.Name)." }
                if (Test-Path -LiteralPath $historyHostRoot) { Remove-Item -LiteralPath $historyHostRoot -Recurse -Force }
                Expand-Archive -LiteralPath $historyHostZip -DestinationPath $historyHostRoot -Force
                & dotnet run --project $validatorProject --no-restore -- --oracle $oracleHostPath --history $historyHostRoot --output $realIoEvidencePath 2>&1 | Tee-Object -FilePath (Join-Path $guestArtifactDirectory 'real-io-validator.log')
                if ($LASTEXITCODE -ne 0) { throw "Real-I/O oracle comparison failed for $($definition.Name) with exit code ${LASTEXITCODE}." }
                $realIo = Get-Content -Raw -Encoding UTF8 -LiteralPath $realIoEvidencePath | ConvertFrom-Json
                if (-not [bool]$realIo.AcceptanceEligible -or [string]$realIo.Status -ne 'PASSED') { throw "Real-I/O evidence was not eligible for $($definition.Name)." }
                Add-Stage "AgentIntegration-$($definition.Name)" 'PASSED' 'The guest Agent ran against the marked data volume; its durable history was retrieved and compared with the real workload oracle.' $realIoEvidencePath
            } else {
                Add-Stage "AgentIntegration-$($definition.Name)" 'NOT_EXECUTED' 'GuestAgentExecutable was not supplied; workload-only execution cannot satisfy product real-I/O acceptance.'
            }
            Add-Stage $definition.Name 'PASSED' 'Real guest volume formatting, dual marker creation, workload execution, and oracle retrieval completed.' $oracleHostPath
        }
        catch {
            Add-Stage $definition.Name 'FAILED' $_.Exception.Message $guestArtifactDirectory
            throw
        }
        finally {
            try {
                if ((Get-VBoxVmState $definition.Name) -ne 'poweroff') {
                    Stop-TestLabVm -Name $definition.Name
                }
            } catch { [void]$cleanupFailures.Add("$($definition.Name) stop: $($_.Exception.Message)") }
            $active = @($activeVhdx | Where-Object VmName -eq $definition.Name | Select-Object -First 1)
            if ($active.Count -eq 1) {
                if (-not $markerVerified) {
                    [void]$cleanupFailures.Add("$($definition.Name) data VHDX cleanup refused because the guest TestLab marker was not verified for TestId=$RunId and Role=$DataRole.")
                } else { try {
                    & (Join-Path $PSScriptRoot 'Remove-TestDataVhdx.ps1') -VmName $definition.Name -VhdxPath $active[0].Path -TestId $RunId -Role $DataRole -ConfigPath $ConfigPath -Apply | Out-File -LiteralPath (Join-Path $guestArtifactDirectory 'remove-vhdx.log') -Encoding UTF8
                    if ($LASTEXITCODE -ne 0) { throw "Data VHDX removal returned exit code $LASTEXITCODE." }
                    [void]$activeVhdx.Remove($active[0])
                } catch { [void]$cleanupFailures.Add("$($definition.Name) data VHDX cleanup: $($_.Exception.Message)") } }
            }
        }
    }

    if ($ExplorerEvidencePath -and (Test-Path -LiteralPath $ExplorerEvidencePath -PathType Leaf)) {
        $correlationOutput = if ([string]::IsNullOrWhiteSpace($CorrelationEvidencePath)) { Join-Path $artifactDirectory 'agent-explorer-correlation.json' } else { [IO.Path]::GetFullPath($CorrelationEvidencePath) }
        if ($null -ne $agentSource -and $null -ne $windows11OracleHostPath -and $null -ne $windows11HistoryHostRoot -and (Test-Path -LiteralPath $windows11OracleHostPath -PathType Leaf) -and (Test-Path -LiteralPath $windows11HistoryHostRoot -PathType Container)) {
            $correlationEnvironmentPath = Join-Path $artifactDirectory 'correlation-environment.json'
            $correlationEnvironment = [ordered]@{
                TargetOs = 'Windows11'
                VmName = 'SC-Test-W11-VBox'
                ExecutionMode = 'TestLab'
                AgentHostMode = 'TestLab'
                Diagnostic = $false
                AgentHistoryPath = [IO.Path]::GetFullPath($windows11HistoryHostRoot)
                AgentExecutablePath = [IO.Path]::GetFullPath($agentSource)
                WorkloadExecutablePath = [IO.Path]::GetFullPath($workloadSource)
                WorkloadOraclePath = [IO.Path]::GetFullPath($windows11OracleHostPath)
                ExplorerEvidencePath = [IO.Path]::GetFullPath($ExplorerEvidencePath)
            }
            $correlationEnvironment | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath $correlationEnvironmentPath
            $correlationProject = Join-Path $repositoryRoot 'tools/StorageChronicle.LiveCorrelationValidator/StorageChronicle.LiveCorrelationValidator.csproj'
            if (-not (Test-Path -LiteralPath $correlationProject -PathType Leaf)) { throw "Live correlation validator project is missing: $correlationProject" }
            & dotnet run --project $correlationProject --configuration Release --no-restore -- --oracle $windows11OracleHostPath --history $windows11HistoryHostRoot --explorer $ExplorerEvidencePath --environment $correlationEnvironmentPath --output $correlationOutput 2>&1 | Tee-Object -FilePath (Join-Path $artifactDirectory 'agent-explorer-correlation.log')
            $correlationExitCode = $LASTEXITCODE
            if ($correlationExitCode -eq 0) {
                $correlation = Get-Content -Raw -Encoding UTF8 -LiteralPath $correlationOutput | ConvertFrom-Json
                if ([string]$correlation.Status -ne 'PASSED' -or -not [bool]$correlation.AcceptanceEligible) { throw 'Live correlation validator returned a non-eligible artifact.' }
                Add-Stage 'explorer-correlation' 'PASSED' 'The real Windows 11 TestLab Agent history, workload oracle, and independent Explorer evidence passed the strict correlation validator.' $correlationOutput
            } else {
                Add-Stage 'explorer-correlation' 'FAILED' "Live correlation validator exited with code $correlationExitCode." $correlationOutput
            }
        } else {
            Add-Stage 'explorer-correlation' 'REQUIRES_REVIEW' 'Explorer evidence was supplied, but a Windows 11 Agent history/oracle pair was not produced; no live correlation claim is made.' ([IO.Path]::GetFullPath($ExplorerEvidencePath))
        }
    } else { Add-Stage 'explorer-correlation' 'NOT_EXECUTED' 'No independent human-assisted Explorer evidence was supplied.' }
    if ($cleanupFailures.Count -gt 0) { throw ('Cleanup failed: ' + ($cleanupFailures -join '; ')) }
    $incompleteStages = @($manifest.Stages | Where-Object { $_.Name -ne 'explorer-correlation' -and $_.Status -in @('NOT_EXECUTED', 'FAILED', 'REQUIRES_REVIEW') })
    $agentStages = @($manifest.Stages | Where-Object Name -like 'AgentIntegration-*')
    $realIoPassed = $null -ne $agentSource -and $agentStages.Count -eq $guests.Count -and @($agentStages | Where-Object Status -ne 'PASSED').Count -eq 0
    $manifest.AgentExecutable = $agentSource
    $manifest.AgentIntegrationExecuted = $null -ne $agentSource
    $manifest.RealIoAcceptance = $realIoPassed
    $manifest.AcceptanceEligible = $realIoPassed -and $incompleteStages.Count -eq 0 -and $cleanupFailures.Count -eq 0
    $manifest.Status = if ($manifest.AcceptanceEligible) { 'COMPLETED_REAL_IO_ACCEPTANCE' } else { 'PARTIAL' }
    $manifest.CompletedUtc = [DateTimeOffset]::UtcNow
    $manifest.CleanupFailures = @($cleanupFailures)
    Write-TestLabJson -Path $manifestPath -Value $manifest
    Write-Output ($manifest | ConvertTo-Json -Depth 12)
    exit $(if ($manifest.AcceptanceEligible) { 0 } else { 2 })
}
catch {
    $manifest.Status = 'FAILED'
    $manifest.Error = $_.Exception.Message
    $manifest.CleanupFailures = @($cleanupFailures)
    $manifest.CompletedUtc = [DateTimeOffset]::UtcNow
    Write-TestLabJson -Path $manifestPath -Value $manifest
    Write-Error $_.Exception.Message
    exit 1
}
