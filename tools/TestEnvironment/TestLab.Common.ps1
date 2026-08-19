[CmdletBinding()]
param()

Set-StrictMode -Version Latest

function Get-TestLabConfig {
    param([string]$ConfigPath)

    $path = if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
        if ($env:SC_TESTLAB_CONFIG) { $env:SC_TESTLAB_CONFIG } else { Join-Path $PSScriptRoot 'TestLab.local.psd1' }
    } else { $ConfigPath }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "TestLab configuration is not present: $path. Obtain user approval and create a local ignored config."
    }

    $config = Import-PowerShellDataFile -LiteralPath $path
    foreach ($key in @('Root', 'Windows11Iso')) {
        if (-not $config.ContainsKey($key) -or [string]::IsNullOrWhiteSpace([string]$config[$key])) {
            throw "TestLab configuration key '$key' is required."
        }
    }
    $config.Root = [IO.Path]::GetFullPath([string]$config.Root)
    $config.Windows11Iso = [IO.Path]::GetFullPath([string]$config.Windows11Iso)
    $config.Windows10Iso = if ($config.ContainsKey('Windows10Iso') -and -not [string]::IsNullOrWhiteSpace([string]$config.Windows10Iso)) { [IO.Path]::GetFullPath([string]$config.Windows10Iso) } else { $null }
    return $config
}

function Assert-TestLabRoot {
    param([Parameter(Mandatory = $true)][string]$Root)

    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw "TestLab root does not exist: $full" }
    $rootOfVolume = [IO.Path]::GetPathRoot($full).TrimEnd('\')
    if ($full.Equals($rootOfVolume, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to use a volume root as TestLab root: $full" }
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')
    $protected = @(
        $repo,
        [Environment]::GetFolderPath('MyDocuments'),
        [Environment]::GetFolderPath('CommonApplicationData'),
        [Environment]::GetFolderPath('ProgramFiles'),
        $env:WINDIR) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') }
    foreach ($item in $protected) {
        if ($full.Equals($item, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($item + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing a protected or repository TestLab root: $full"
        }
    }
    return $full
}

function Assert-ExistingIso {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Label)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label is not an existing local ISO: $Path" }
    if ([IO.Path]::GetExtension($Path) -ine '.iso') { throw "$Label must have an .iso extension: $Path" }
}

function Assert-HyperVMutationPrerequisites {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'The TestLab requires Windows.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'An elevated PowerShell is required; the script will not self-elevate.' }
    if (-not (Get-Module -ListAvailable -Name Hyper-V)) { throw 'Hyper-V PowerShell module is unavailable; no VM operation was attempted.' }
    Import-Module Hyper-V -ErrorAction Stop
    $vmms = Get-Service -Name vmms -ErrorAction SilentlyContinue
    if ($null -eq $vmms -or $vmms.Status -ne 'Running') { throw 'The Hyper-V VMMS service is not running; no VM operation was attempted.' }
}

function Get-TestLabVmDefinition {
    param([Parameter(Mandatory = $true)][ValidateSet('Windows11', 'Windows10')][string]$Guest)
    if ($Guest -eq 'Windows11') {
        return [pscustomobject]@{ Name = 'SC-Test-W11'; MinimumMemoryGiB = 2; StartupMemoryGiB = 4; MaximumMemoryGiB = 6; VhdxSizeGiB = 80; IsoKey = 'Windows11Iso'; SecureBoot = $true; Tpm = $true }
    }
    return [pscustomobject]@{ Name = 'SC-Test-W10'; MinimumMemoryGiB = 2; StartupMemoryGiB = 4; MaximumMemoryGiB = 6; VhdxSizeGiB = 64; IsoKey = 'Windows10Iso'; SecureBoot = $false; Tpm = $false }
}

function Assert-ExactTestLabVm {
    param([Parameter(Mandatory = $true)][string]$Name)
    if ($Name -notin @('SC-Test-W11', 'SC-Test-W10')) { throw "Only the approved TestLab VM names are allowed: SC-Test-W11 or SC-Test-W10." }
    $vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $vm) { throw "Approved TestLab VM does not exist: $Name" }
    return $vm
}

function Assert-TestLabVmDisks {
    param(
        [Parameter(Mandatory = $true)]$Vm,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $disks = @(Get-VMHardDiskDrive -VMName $Vm.Name -ErrorAction Stop)
    if ($disks.Count -eq 0) { throw "The approved TestLab VM has no virtual disks: $($Vm.Name)" }
    foreach ($disk in $disks) {
        if ([string]::IsNullOrWhiteSpace([string]$disk.Path)) { throw "The approved TestLab VM has a virtual disk without a host path: $($Vm.Name)" }
        $diskPath = [IO.Path]::GetFullPath([string]$disk.Path)
        if (-not ($diskPath.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase) -or $diskPath.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase))) {
            throw "The approved TestLab VM disk is outside the approved root: $diskPath"
        }
    }
}

function Assert-PathUnderRoot {
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$Path)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not ($pathFull.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase) -or $pathFull.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase))) {
        throw "Path is outside the approved TestLab root: $pathFull"
    }
    return $pathFull
}

function New-TestLabArtifactDirectory {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot, [string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $path = Join-Path $RepositoryRoot "artifacts\acceptance\testlab\$RunId"
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    return $path
}

function Write-TestLabJson {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)]$Value)
    $parent = Split-Path -Parent $Path
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
}
