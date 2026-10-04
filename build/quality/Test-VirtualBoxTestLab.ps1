[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repositoryRoot 'tools/TestEnvironment/VirtualBox.Common.ps1')

$testRunId = [guid]::NewGuid().ToString('N')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('StorageChronicle.VirtualBoxContract.' + $testRunId)
New-Item -ItemType Directory -Path $testRoot | Out-Null
$testOwnerMarker = Join-Path $testRoot '.test-owner.json'
if (Test-Path -LiteralPath $testOwnerMarker) { throw 'The new VirtualBox contract fixture unexpectedly already has an owner marker.' }
@{ schema = 'StorageChronicle.VirtualBoxContractFixture.v1'; runId = $testRunId } | ConvertTo-Json -Compress | Set-Content -LiteralPath $testOwnerMarker -Encoding UTF8
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

function Assert-RetiredVirtualBoxEntryPoint {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    $path = Join-Path $repositoryRoot $RelativePath
    $source = Get-Content -Raw -LiteralPath $path
    if ($source -notmatch 'Retired under Requirements? .*37') { throw "Legacy VM entry point is not marked retired: $RelativePath" }
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw "Retired VM entry point has PowerShell parse errors: $RelativePath" }
    foreach ($command in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true)) {
        if ($command.GetCommandName() -notin @('Write-Error', 'exit')) { throw "Retired VM entry point contains executable command '$($command.GetCommandName())': $RelativePath" }
    }
    foreach ($invocation in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] }, $true)) {
        throw "Retired VM entry point contains a method invocation '$($invocation.Extent.Text)': $RelativePath"
    }
    foreach ($assignment in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)) {
        if ($assignment.Left.Extent.Text -ne '$ErrorActionPreference' -or $assignment.Right.Extent.Text -ne "'Stop'") {
            throw "Retired VM entry point contains an unexpected assignment '$($assignment.Extent.Text)': $RelativePath"
        }
    }
}

try {
    Assert-ContractCase 'legacy VM creation entry point is inert' {
        Assert-RetiredVirtualBoxEntryPoint 'tools/TestEnvironment/Initialize-TestLab.ps1'
    }
    Assert-ContractCase 'legacy guest command entry point is inert' {
        Assert-RetiredVirtualBoxEntryPoint 'tools/TestEnvironment/Invoke-TestLabCommand.ps1'
    }
    Assert-ContractCase 'legacy snapshot reset entry point is inert' {
        Assert-RetiredVirtualBoxEntryPoint 'tools/TestEnvironment/Reset-TestVm.ps1'
    }
    foreach ($retiredEntryPoint in @(
        'tools/TestEnvironment/Copy-TestArtifactsToVm.ps1',
        'tools/TestEnvironment/Copy-TestResultsFromVm.ps1',
        'tools/TestEnvironment/Invoke-Windows10StageACapabilityChecks.ps1',
        'tools/TestEnvironment/Test-Windows10StageACapability.ps1',
        'tools/TestEnvironment/Compose-Windows10StageA.ps1',
        'tools/TestEnvironment/Run-VirtualBoxInstallerAcceptance.ps1',
        'tools/TestEnvironment/Test-TestLabPrerequisites.ps1',
        'tools/PhysicalAcceptance/Invoke-VirtualBoxInstallerCase.ps1')) {
        Assert-ContractCase "retired legacy VM/Stage A entry point is inert: $retiredEntryPoint" {
            Assert-RetiredVirtualBoxEntryPoint $retiredEntryPoint
        }
    }
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
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
        $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        $owner = Get-Content -Raw -LiteralPath $testOwnerMarker | ConvertFrom-Json
        if ([IO.Path]::GetDirectoryName($resolvedRoot) -ne $expectedParent -or
            [IO.Path]::GetFileName($resolvedRoot) -ne ('StorageChronicle.VirtualBoxContract.' + $testRunId) -or
            $owner.schema -ne 'StorageChronicle.VirtualBoxContractFixture.v1' -or $owner.runId -ne $testRunId) {
            throw 'Refusing to delete the VirtualBox contract fixture because its run ownership could not be verified.'
        }
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
