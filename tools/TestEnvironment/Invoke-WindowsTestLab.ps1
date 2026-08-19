[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$RunId = ([guid]::NewGuid().ToString('N')),
    [ValidateSet('Windows11', 'Windows10', 'Both')][string]$Target = 'Both',
    [Parameter(Mandatory = $true)][string]$GuestWorkloadExecutable,
    [string]$GuestDataRoot = 'D:\StorageChronicleTestData',
    [ValidateSet('Workload', 'Mft', 'NonNtfs')][string]$DataRole = 'Workload',
    [ValidateRange(1, 1000000)][int]$WorkloadCount = 10000,
    [string]$ExplorerEvidencePath,
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
    StartedUtc = [DateTimeOffset]::UtcNow
    DataRole = $DataRole
    WorkloadCount = $WorkloadCount
    Stages = @()
    ArtifactDirectory = $artifactDirectory
}
$activeVhdx = [System.Collections.Generic.List[object]]::new()
$cleanupFailures = [System.Collections.Generic.List[string]]::new()

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
    if ($LASTEXITCODE -ne 0) { throw "PowerShell Direct command failed on ${VmName}: $($output -join [Environment]::NewLine)" }
    return $output
}

function Get-GuestDataDefinition {
    switch ($DataRole) {
        'Mft' { return [ordered]@{ Label = 'SC_TEST_MFT_VOLUME'; FileSystem = 'NTFS'; Scenario = 'mft' } }
        'NonNtfs' { return [ordered]@{ Label = 'SC_TEST_NONNTFS_VOLUME'; FileSystem = 'exFAT'; Scenario = 'basic' } }
        default { return [ordered]@{ Label = 'SC_TEST_VOLUME'; FileSystem = 'NTFS'; Scenario = 'basic' } }
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

    if (-not $Apply) {
        Add-Stage 'preflight' 'READY_FOR_USER_APPLY' 'Configuration, local ISO paths, and workload artifact are valid; no VM or guest operation was run.'
        foreach ($guest in $guests) { Add-Stage $guest 'NOT_EXECUTED' 'Pass -Apply only after the user approves baseline restore, data VHDX creation, guest format, workload, and cleanup.' }
        Add-Stage 'explorer-correlation' 'NOT_EXECUTED' 'Real Explorer actions require an interactive human-assisted session and an independent artifact.'
        $manifest.Status = 'READY_FOR_USER_APPLY'
        Write-TestLabJson -Path $manifestPath -Value $manifest
        Write-Output ($manifest | ConvertTo-Json -Depth 12)
        exit 2
    }

    Assert-HyperVMutationPrerequisites
    Add-Stage 'preflight' 'PASSED' 'Hyper-V and VMMS were available on the elevated host.'
    $guestWorkloadDestination = 'C:\StorageChronicleTest\StorageChronicle.FileMutationWorkload.exe'
    $definitionData = Get-GuestDataDefinition

    foreach ($guest in $guests) {
        $definition = Get-TestLabVmDefinition -Guest $guest
        $vm = Assert-ExactTestLabVm -Name $definition.Name
        Assert-TestLabVmDisks -Vm $vm -Root $root
        $safeRunId = $RunId -replace '[^A-Za-z0-9_.-]', '-'
        $vhdxPath = Assert-PathUnderRoot -Root $root -Path (Join-Path $root "data\$($definition.Name)\$safeRunId-$DataRole.vhdx")
        $guestArtifactDirectory = Join-Path $artifactDirectory $definition.Name
        New-Item -ItemType Directory -Force -Path $guestArtifactDirectory | Out-Null
        $oracleGuestPath = Join-Path $GuestDataRoot "$RunId-oracle.json"
        $oracleHostPath = Join-Path $guestArtifactDirectory 'workload-oracle.json'
        $guestCommand = "& $(Quote-GuestLiteral $guestWorkloadDestination) --root $(Quote-GuestLiteral $GuestDataRoot) --oracle $(Quote-GuestLiteral $oracleGuestPath) --scenario $(Quote-GuestLiteral $definitionData.Scenario) --count $WorkloadCount --run-id $(Quote-GuestLiteral $RunId)"
        $markerVerified = $false
        try {
            & (Join-Path $PSScriptRoot 'Reset-TestVm.ps1') -Name $definition.Name -CheckpointName 'SC-CLEAN-BASELINE' -ConfigPath $ConfigPath -Apply | Out-File -LiteralPath (Join-Path $guestArtifactDirectory 'reset.log') -Encoding UTF8
            if ($LASTEXITCODE -ne 0) { throw "Baseline reset failed for $($definition.Name)." }
            $newArgs = @{ VmName = $definition.Name; Role = $DataRole; TestId = $RunId; ConfigPath = $ConfigPath; Apply = $true }
            & (Join-Path $PSScriptRoot 'New-TestDataVhdx.ps1') @newArgs | Out-File -LiteralPath (Join-Path $guestArtifactDirectory 'new-vhdx.log') -Encoding UTF8
            if ($LASTEXITCODE -ne 0) { throw "Data VHDX creation failed for $($definition.Name)." }
            [void]$activeVhdx.Add([pscustomobject]@{ VmName = $definition.Name; Path = $vhdxPath })
            Start-VM -Name $definition.Name -ErrorAction Stop | Out-Null
            Add-Stage $definition.Name 'STARTED' 'Baseline restored, disposable VHDX attached, and the approved VM started.' $vhdxPath
            Invoke-GuestCommand -VmName $definition.Name -Command "New-Item -ItemType Directory -Force -Path $(Quote-GuestLiteral ([IO.Path]::GetDirectoryName($guestWorkloadDestination))) | Out-Null" | Out-Null
            & (Join-Path $PSScriptRoot 'Copy-TestArtifactsToVm.ps1') -VmName $definition.Name -SourcePath $workloadSource -DestinationPath $guestWorkloadDestination -Credential $Credential -ConfigPath $ConfigPath | Out-File -LiteralPath (Join-Path $guestArtifactDirectory 'copy-workload.log') -Encoding UTF8
            if ($LASTEXITCODE -ne 0) { throw "Workload transfer failed for $($definition.Name)." }
            Initialize-GuestDataVolume -VmName $definition.Name -Root $GuestDataRoot
            Invoke-GuestCommand -VmName $definition.Name -Command $guestCommand | Out-File -LiteralPath (Join-Path $guestArtifactDirectory 'workload.log') -Encoding UTF8
            Assert-GuestDataMarker -VmName $definition.Name -Root $GuestDataRoot
            $markerVerified = $true
            & (Join-Path $PSScriptRoot 'Copy-TestResultsFromVm.ps1') -VmName $definition.Name -SourcePath $oracleGuestPath -DestinationPath $oracleHostPath -Credential $Credential -ConfigPath $ConfigPath | Out-File -LiteralPath (Join-Path $guestArtifactDirectory 'copy-oracle.log') -Encoding UTF8
            if ($LASTEXITCODE -ne 0) { throw "Oracle retrieval failed for $($definition.Name)." }
            Add-Stage $definition.Name 'PASSED' 'Real guest volume formatting, dual marker creation, workload execution, and oracle retrieval completed.' $oracleHostPath
        }
        catch {
            Add-Stage $definition.Name 'FAILED' $_.Exception.Message $guestArtifactDirectory
            throw
        }
        finally {
            if ($vm.State -ne 'Off') {
                try { Stop-VM -Name $definition.Name -TurnOff -Confirm:$false -ErrorAction Stop } catch { [void]$cleanupFailures.Add("$($definition.Name) stop: $($_.Exception.Message)") }
            }
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

    if ($ExplorerEvidencePath -and (Test-Path -LiteralPath $ExplorerEvidencePath -PathType Leaf)) { Add-Stage 'explorer-correlation' 'REQUIRES_REVIEW' 'An evidence file was supplied; correlation must be checked for false Exact=0 and Unknown/Create correctness.' ([IO.Path]::GetFullPath($ExplorerEvidencePath)) }
    else { Add-Stage 'explorer-correlation' 'NOT_EXECUTED' 'No independent human-assisted Explorer evidence was supplied.' }
    if ($cleanupFailures.Count -gt 0) { throw ('Cleanup failed: ' + ($cleanupFailures -join '; ')) }
    $manifest.Status = if (@($manifest.Stages | Where-Object Status -in @('NOT_EXECUTED', 'FAILED', 'REQUIRES_REVIEW')).Count -eq 0) { 'COMPLETED_NEEDS_PRODUCT_ASSERTION' } else { 'PARTIAL' }
    $manifest.CompletedUtc = [DateTimeOffset]::UtcNow
    $manifest.CleanupFailures = @($cleanupFailures)
    Write-TestLabJson -Path $manifestPath -Value $manifest
    Write-Output ($manifest | ConvertTo-Json -Depth 12)
    exit 0
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
