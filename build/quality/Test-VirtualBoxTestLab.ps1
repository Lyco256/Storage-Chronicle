[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repositoryRoot 'tools/TestEnvironment/VirtualBox.Common.ps1')

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('StorageChronicle.VirtualBoxContract.' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$fakeOsDisk = Join-Path $testRoot 'SC-Test-W11-VBox\os.vdi'
$fakeW10Disk = Join-Path $testRoot 'SC-Test-W10-VBox\os.vdi'
$vmStates = @{ 'SC-Test-W11-VBox' = 'poweroff'; 'SC-Test-W10-VBox' = 'poweroff' }
$vmMemory = 4096
$fakeDiskExtension = 'vdi'
$omitNic8 = $false
$passwordObserved = $false
$passed = 0
$failed = [System.Collections.Generic.List[string]]::new()

function New-FakeVmOutput {
    param([Parameter(Mandatory = $true)][string]$Name)
    $disk = if ($Name -eq 'SC-Test-W11-VBox') { $fakeOsDisk } else { $fakeW10Disk }
    $osType = if ($Name -eq 'SC-Test-W11-VBox') { 'Windows11_64' } else { 'Windows10_64' }
    $tpm = if ($Name -eq 'SC-Test-W11-VBox') { '2.0' } else { 'none' }
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in @(
            ('name="' + $Name + '"')
            ('ostype="' + $osType + '"')
            "memory=$vmMemory"
            'cpus=2'
            'firmware="efi"'
            ('tpm-type="' + $tpm + '"')
            ('VMState="' + $vmStates[$Name] + '"')
            'clipboard-mode="disabled"'
            'draganddrop="disabled"'
            'accelerate3d="off"'
            'audio-enabled="off"'
            'usb="off"'
            'vrde="off"'
            'nic1="none"'
            'nic2="none"'
            'nic3="none"'
            'nic4="none"'
            'nic5="none"'
            'nic6="none"'
            'nic7="none"'
            'SATA-0-0="' + ($disk -replace '\.vdi$', ".$fakeDiskExtension") + '"'
        )) { [void]$lines.Add($entry) }
    if ($omitNic8) { return ($lines -join "`r`n") }
    [void]$lines.Add('nic8="none"')
    return ($lines -join "`r`n")
}

function Invoke-VBoxManage {
    param([Parameter(Mandatory = $true)][string[]]$Arguments, [switch]$AllowNonZero)
    $operation = $Arguments -join ' '
    if ($Arguments[0] -eq 'showvminfo') {
        $name = $Arguments[1]
        if (-not $vmStates.ContainsKey($name)) { return [pscustomobject]@{ ExitCode = 1; Output = ''; Error = 'not found'; Arguments = $Arguments } }
        return [pscustomobject]@{ ExitCode = 0; Output = (New-FakeVmOutput -Name $name); Error = ''; Arguments = $Arguments }
    }
    if ($Arguments[0] -eq 'showmediuminfo') {
        return [pscustomobject]@{ ExitCode = 0; Output = "Format=`"VDI`"`r`nType=`"normal`"`r`nLogicalSize=85899345920"; Error = ''; Arguments = $Arguments }
    }
    if ($Arguments[0] -eq 'guestcontrol') {
        $passwordIndex = [array]::IndexOf($Arguments, '--passwordfile')
        if ($passwordIndex -lt 0 -or -not (Test-Path -LiteralPath $Arguments[$passwordIndex + 1] -PathType Leaf)) { throw 'guestcontrol password file was not present during invocation' }
        $script:passwordObserved = $true
        return [pscustomobject]@{ ExitCode = 0; Output = 'guest-admin'; Error = ''; Arguments = $Arguments }
    }
    if ($Arguments[0] -eq 'snapshot') { return [pscustomobject]@{ ExitCode = 1; Output = ''; Error = 'snapshot not present'; Arguments = $Arguments } }
    return [pscustomobject]@{ ExitCode = 0; Output = ''; Error = ''; Arguments = $Arguments }
}

function Assert-ContractCase {
    param([Parameter(Mandatory = $true)][string]$Name, [Parameter(Mandatory = $true)][scriptblock]$Body)
    try { & $Body; $script:passed++ }
    catch { [void]$script:failed.Add("${Name}: $($_.Exception.Message)") }
}

try {
    Assert-ContractCase 'exact dynamic Windows 11 profile passes' {
        Assert-TestLabVmProfile -Name 'SC-Test-W11-VBox' -Root $testRoot | Out-Null
        Assert-TestLabVmDisks -Vm (Assert-ExactTestLabVm -Name 'SC-Test-W11-VBox') -Root $testRoot
        Assert-VBoxSafeSettings -Name 'SC-Test-W11-VBox'
    }
    Assert-ContractCase 'missing safety property fails closed' {
        $script:omitNic8 = $true
        try { Assert-VBoxSafeSettings -Name 'SC-Test-W11-VBox'; throw 'missing nic8 was accepted' }
        catch { if ($_.Exception.Message -notmatch 'nic8') { throw } }
        finally { $script:omitNic8 = $false }
    }
    Assert-ContractCase 'wrong memory profile fails closed' {
        $script:vmMemory = 2048
        try { Assert-TestLabVmProfile -Name 'SC-Test-W11-VBox' -Root $testRoot; throw 'wrong memory was accepted' }
        catch { if ($_.Exception.Message -notmatch 'memory') { throw } }
        finally { $script:vmMemory = 4096 }
    }
    Assert-ContractCase 'simultaneous TestLab VM fails closed' {
        $script:vmStates['SC-Test-W10-VBox'] = 'running'
        try { Assert-TestLabVmExclusive -Name 'SC-Test-W11-VBox'; throw 'simultaneous VM was accepted' }
        catch { if ($_.Exception.Message -notmatch 'Only one') { throw } }
        finally { $script:vmStates['SC-Test-W10-VBox'] = 'poweroff' }
    }
    Assert-ContractCase 'non-VDI disk fails closed' {
        $script:fakeDiskExtension = 'vhdx'
        try { Assert-TestLabVmDisks -Vm (Assert-ExactTestLabVm -Name 'SC-Test-W11-VBox') -Root $testRoot; throw 'non-VDI disk was accepted' }
        catch { if ($_.Exception.Message -notmatch 'VDI') { throw } }
        finally { $script:fakeDiskExtension = 'vdi' }
    }
    Assert-ContractCase 'guest password file is removed after guestcontrol' {
        $credential = [pscredential]::new('guest-admin', (ConvertTo-SecureString 'test-only-secret' -AsPlainText -Force))
        Invoke-VBoxGuestControl -VmName 'SC-Test-W11-VBox' -Credential $credential -Executable 'C:\Windows\System32\whoami.exe' -TempRoot $testRoot | Out-Null
        if (-not $passwordObserved) { throw 'guestcontrol was not invoked' }
        if (@(Get-ChildItem -LiteralPath $testRoot -Filter '.vbox-password-*' -File -ErrorAction SilentlyContinue).Count -ne 0) { throw 'guest password file remained after guestcontrol' }
    }
    Write-Output ("VirtualBoxContractTests Passed={0} Failed={1}" -f $passed, $failed.Count)
    if ($failed.Count -gt 0) { $failed | ForEach-Object { Write-Error $_ }; exit 1 }
    exit 0
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
