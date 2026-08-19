[CmdletBinding()]
param(
    [string]$ConfigPath,
    [ValidateSet('Windows11', 'Windows10', 'Both')][string]$Target = 'Windows11',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestLab.Common.ps1')

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifactDirectory = New-TestLabArtifactDirectory -RepositoryRoot $repositoryRoot -RunId $runId
$manifestPath = Join-Path $artifactDirectory 'initialize-testlab.json'
$manifest = [ordered]@{
    Schema = 'StorageChronicle.TestLabInitialization.v1'
    Status = 'NOT_EXECUTED'
    Apply = [bool]$Apply
    Target = $Target
    StartedUtc = [DateTimeOffset]::UtcNow
    VMs = @()
    ArtifactDirectory = $artifactDirectory
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

    if (-not $Apply) {
        $manifest.Status = 'READY_FOR_USER_APPLY'
        $manifest.Reason = 'Read-only validation completed. Re-run with -Apply only after the user approves VM creation.'
        Write-TestLabJson -Path $manifestPath -Value $manifest
        Write-Output ($manifest | ConvertTo-Json -Depth 10)
        exit 2
    }

    Assert-HyperVMutationPrerequisites
    foreach ($guest in $guests) {
        $definition = Get-TestLabVmDefinition -Guest $guest
        $osPath = Assert-PathUnderRoot -Root $root -Path (Join-Path $root ($definition.Name + '\os.vhdx'))
        $isoPath = [string]$config[$definition.IsoKey]
            $existing = Get-VM -Name $definition.Name -ErrorAction SilentlyContinue
            $created = $false
        if ($null -eq $existing) {
            if (Test-Path -LiteralPath $osPath) { throw "Refusing to reuse an untracked OS VHDX: $osPath" }
            $existing = New-VM -Name $definition.Name -Generation 2 -MemoryStartupBytes ($definition.StartupMemoryGiB * 1GB) -NewVHDPath $osPath -NewVHDSizeBytes ($definition.VhdxSizeGiB * 1GB) -Path (Join-Path $root $definition.Name)
            $created = $true
        }

            $actual = Get-VM -Name $definition.Name
            if ($actual.Generation -ne 2) { throw "The existing VM has the wrong generation: $($definition.Name)" }
            Assert-TestLabVmDisks -Vm $actual -Root $root
            Set-VMMemory -VMName $definition.Name -DynamicMemoryEnabled $true -MinimumBytes ($definition.MinimumMemoryGiB * 1GB) -StartupBytes ($definition.StartupMemoryGiB * 1GB) -MaximumBytes ($definition.MaximumMemoryGiB * 1GB)
        Set-VMProcessor -VMName $definition.Name -Count 2
        $adapters = @(Get-VMNetworkAdapter -VMName $definition.Name -ErrorAction SilentlyContinue)
        if ($adapters.Count -gt 0) { $adapters | Remove-VMNetworkAdapter -Confirm:$false }
        if ($definition.SecureBoot) { Set-VMFirmware -VMName $definition.Name -EnableSecureBoot On }
        else { Set-VMFirmware -VMName $definition.Name -EnableSecureBoot Off }
        if ($definition.Tpm) { Enable-VMTPM -VMName $definition.Name }
        $dvd = Get-VMDvdDrive -VMName $definition.Name -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $dvd) { $dvd = Add-VMDvdDrive -VMName $definition.Name -Path $isoPath -Passthru }
        else { Set-VMDvdDrive -VMName $definition.Name -Path $isoPath }
            $manifest.VMs += [ordered]@{ Name = $definition.Name; Created = $created; OsVhdx = $osPath; Iso = $isoPath; Generation = 2; ProcessorCount = 2; DynamicMemory = '2-6GiB'; StartupMemory = '4GiB'; Network = 'Disconnected'; SecureBoot = $definition.SecureBoot; Vtpm = $definition.Tpm }
    }

    $manifest.Status = 'CREATED_WAITING_FOR_GUEST_INSTALL'
    $manifest.Reason = 'OS installation, guest setup, and baseline checkpoint require explicit user action.'
    Write-TestLabJson -Path $manifestPath -Value $manifest
    Write-Output ($manifest | ConvertTo-Json -Depth 10)
    exit 0
}
catch {
    $manifest.Status = 'FAILED'
    $manifest.Error = $_.Exception.Message
    Write-TestLabJson -Path $manifestPath -Value $manifest
    Write-Error $_.Exception.Message
    exit 1
}
