[CmdletBinding()]
param(
    [string]$ConfigPath,
    [ValidateSet('Windows11', 'Windows10', 'Both')][string]$Target = 'Windows11',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'TestLab.Common.ps1')

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifactDirectory = New-TestLabArtifactDirectory -RepositoryRoot $repositoryRoot -RunId $runId
$manifestPath = Join-Path $artifactDirectory 'initialize-testlab.json'
$manifest = [ordered]@{ Schema = 'StorageChronicle.VirtualBoxTestLabInitialization.v1'; Status = 'NOT_EXECUTED'; Apply = [bool]$Apply; Target = $Target; StartedUtc = [DateTimeOffset]::UtcNow; VMs = @(); ArtifactDirectory = $artifactDirectory; AcceptanceEligible = $false }

function Add-VirtualBoxVm {
    param([Parameter(Mandatory = $true)]$Config, [Parameter(Mandatory = $true)]$Definition, [Parameter(Mandatory = $true)][string]$Root)
    $vmDirectory = Assert-PathUnderRoot -Root $Root -Path (Join-Path $Root $Definition.Name)
    $diskPath = Assert-PathUnderRoot -Root $Root -Path (Join-Path $vmDirectory 'os.vdi')
    New-Item -ItemType Directory -Force -Path $vmDirectory | Out-Null
    $existing = Invoke-VBoxManage @('showvminfo', $Definition.Name, '--machinereadable') -AllowNonZero
    $created = $false
    if ($existing.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $diskPath) { throw "Refusing to reuse an unregistered OS disk: $diskPath" }
        Invoke-VBoxManage @('createvm', '--name', $Definition.Name, '--ostype', $Definition.GuestOsType, '--basefolder', $vmDirectory, '--register') | Out-Null
        Invoke-VBoxManage @('createmedium', 'disk', '--filename', $diskPath, '--size', ([int]($Definition.DiskSizeGiB * 1024)), '--format', 'VDI', '--variant', 'Standard') | Out-Null
        Invoke-VBoxManage @('storagectl', $Definition.Name, '--name', 'SATA', '--add', 'sata', '--controller', 'IntelAhci') | Out-Null
        Invoke-VBoxManage @('storageattach', $Definition.Name, '--storagectl', 'SATA', '--port', '0', '--device', '0', '--type', 'hdd', '--medium', $diskPath) | Out-Null
        Invoke-VBoxManage @('storagectl', $Definition.Name, '--name', 'IDE', '--add', 'ide') | Out-Null
        $created = $true
    } else {
        $vm = Assert-ExactTestLabVm -Name $Definition.Name
        Assert-TestLabVmDisks -Vm $vm -Root $Root
    }
    $iso = [string]$Config[$Definition.IsoKey]
    Assert-ExistingIso -Path $iso -Label "$($Definition.TargetOs) ISO"
    Invoke-VBoxManage @('modifyvm', $Definition.Name, '--memory', '4096', '--cpus', '2', '--firmware', 'efi', '--vram', '64', '--accelerate3d', 'off', '--audio-enabled', 'off', '--audio-driver', 'none', '--usb', 'off', '--clipboard-mode', 'disabled', '--draganddrop', 'disabled', '--nic1', 'nat', '--boot1', 'dvd', '--boot2', 'disk') | Out-Null
    if ($Definition.Tpm) { Invoke-VBoxManage @('modifyvm', $Definition.Name, '--tpm-type', '2.0') | Out-Null }
    $dvd = Invoke-VBoxManage @('showvminfo', $Definition.Name, '--machinereadable')
    Invoke-VBoxManage @('storageattach', $Definition.Name, '--storagectl', 'IDE', '--port', '1', '--device', '0', '--type', 'dvddrive', '--medium', $iso) | Out-Null
    Assert-VBoxSafeSettings -Name $Definition.Name -AllowProvisioningNetwork
    return [ordered]@{ Name = $Definition.Name; Created = $created; OsDisk = $diskPath; Iso = $iso; MemoryMiB = 4096; Vcpu = 2; DynamicDisk = $true; Firmware = 'EFI'; Tpm = $Definition.Tpm; Network = 'NAT until baseline, then disconnected'; Snapshot = 'SC-CLEAN-BASELINE required after user guest setup' }
}

try {
    $config = Get-TestLabConfig -ConfigPath $ConfigPath
    $root = Assert-TestLabRoot -Root $config.Root
    $guests = if ($Target -eq 'Both') { @('Windows11', 'Windows10') } else { @($Target) }
    foreach ($guest in $guests) { $definition = Get-TestLabVmDefinition -Guest $guest; if ([string]::IsNullOrWhiteSpace([string]$config[$definition.IsoKey])) { throw "$($definition.TargetOs) ISO is required when the $guest target is selected." }; Assert-ExistingIso -Path ([string]$config[$definition.IsoKey]) -Label "$($definition.TargetOs) ISO" }
    $resource = Assert-VirtualBoxResourceGate -Root $root
    $manifest.Resource = $resource
    if (-not $Apply) {
        $manifest.Status = 'READY_FOR_USER_APPLY'
        $manifest.Reason = 'VirtualBox, safe root, ISO paths, and resource profile were validated. Re-run with -Apply only after the user approves VM creation.'
        $manifest.VMs = @($guests | ForEach-Object { $definition = Get-TestLabVmDefinition -Guest $_; [ordered]@{ Name = $definition.Name; MemoryMiB = 4096; Vcpu = 2; DynamicDiskGiB = $definition.DiskSizeGiB; TargetOs = $definition.TargetOs } })
        Write-TestLabJson -Path $manifestPath -Value $manifest
        Write-Output ($manifest | ConvertTo-Json -Depth 12)
        exit 2
    }
    foreach ($guest in $guests) { $manifest.VMs += Add-VirtualBoxVm -Config $config -Definition (Get-TestLabVmDefinition -Guest $guest) -Root $root }
    $manifest.Status = 'CREATED_WAITING_FOR_GUEST_SETUP'
    $manifest.Reason = 'Complete the user-controlled Windows setup, install matching Guest Additions, verify guestcontrol, disconnect networking, and create exactly one SC-CLEAN-BASELINE snapshot before acceptance.'
    Write-TestLabJson -Path $manifestPath -Value $manifest
    Write-Output ($manifest | ConvertTo-Json -Depth 12)
    exit 0
}
catch {
    $manifest.Status = 'BLOCKED'
    $manifest.Error = $_.Exception.Message
    $manifest.HumanHandoff = [ordered]@{ Blocked = 'VirtualBox TestLab initialization'; Reason = $_.Exception.Message; WhyUserActionIsRequired = 'VM creation, VirtualBox installation, firmware, ISO selection, and guest Windows setup are host/user-controlled operations. The script never self-elevates or bypasses UAC.'; DoThis = @('Complete only the user-approved VirtualBox installation/firmware/ISO/guest setup action described by Requirements 34.', 'Do not install Extension Pack or enable shared folders, shared clipboard, drag-and-drop, raw disks, or host C: access.'); ExpectedResult = 'A manifest with Status=CREATED_WAITING_FOR_GUEST_SETUP is written, followed by one clean baseline snapshot per VM.'; DoNotDo = @('Do not run Codex as Administrator.', 'Do not use unofficial or modified ISO media.'); ResumeCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`" -ConfigPath `"$ConfigPath`" -Target $Target -Apply"; SendBack = @('Only non-secret Status, Error, VM names, VirtualBox version, and preflight fields; no credentials or product keys.') }
    Write-TestLabJson -Path $manifestPath -Value $manifest
    Write-Error $_.Exception.Message
    Write-Output ($manifest | ConvertTo-Json -Depth 12)
    exit 2
}
