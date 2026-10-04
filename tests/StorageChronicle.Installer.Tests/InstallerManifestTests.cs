using Xunit;
using StorageChronicle.InstallerAuthorization;

namespace StorageChronicle.Installer.Tests;

public sealed class InstallerManifestTests
{
    private static readonly string[] ExistingRunValueNames = { "OtherValue", "storagechroniclesessionagent" };
    private static readonly string[] MissingRunValueNames = { "OtherValue", "StorageChronicleSessionAgentBackup" };

    [Fact]
    public void InstallerAuthorizationHelloAcceptsOnlyTheExactChildAndCaseContext()
    {
        var hash = new string('A', 64);
        var nonce = InstallerAuthorizationProtocol.CreateToken();
        Assert.True(InstallerAuthorizationProtocol.ValidateHello(
            "StorageChronicle.InstallerCaseAuthorizationHello.v1", "run-1", "service", "TEST-PC", 1234, 5678, nonce, hash,
            "run-1", "service", "test-pc", 1234, 5678, nonce, hash));
        Assert.False(InstallerAuthorizationProtocol.ValidateHello(
            "StorageChronicle.InstallerCaseAuthorizationHello.v1", "run-1", "service", "TEST-PC", 1235, 5678, nonce, hash,
            "run-1", "service", "test-pc", 1234, 5678, nonce, hash));
        Assert.False(InstallerAuthorizationProtocol.ValidateHello(
            "StorageChronicle.InstallerCaseAuthorizationHello.v1", "run-1", "repair", "TEST-PC", 1234, 5678, nonce, hash,
            "run-1", "service", "test-pc", 1234, 5678, nonce, hash));
        Assert.False(InstallerAuthorizationProtocol.ValidateHello(
            "StorageChronicle.InstallerCaseAuthorizationHello.v1", "run-1", "service", "TEST-PC", 1234, 5678, nonce, hash,
            "run-1", "service", "test-pc", 1234, 5678, nonce, new string('B', 64)));
    }

    [Fact]
    public void InstallerAuthorizationNonceRejectsMismatchAndReplay()
    {
        var nonce = InstallerAuthorizationProtocol.CreateToken();
        var mismatch = new OneTimeNonce(nonce);
        Assert.False(mismatch.TryConsume("not-the-nonce"));
        Assert.False(mismatch.TryConsume(nonce));

        var validOnce = new OneTimeNonce(nonce);
        Assert.True(validOnce.TryConsume(nonce));
        Assert.False(validOnce.TryConsume(nonce));
    }

    [Fact]
    public void UacCancellationMapsToNotExecutedAndOtherLaunchFailuresRemainFailures()
    {
        Assert.Equal("NOT_EXECUTED", InstallerAuthorizationProtocol.MapLaunchFailure(1223));
        Assert.Equal("FAILED", InstallerAuthorizationProtocol.MapLaunchFailure(5));
        Assert.Equal("FAILED", InstallerAuthorizationProtocol.MapLaunchFailure(-1));
    }

    [Fact]
    public void VerifiedPayloadLockRejectsCorruptedPayloadAndReleasesFailedOpen()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "sc-installer-payload-" + Guid.NewGuid().ToString("N") + ".bin");
        var bytes = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        try
        {
            File.WriteAllBytes(path, bytes);
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

            Assert.Throws<InvalidDataException>(() => VerifiedPayloadLock.OpenAndVerify(path, new string('0', 64)));

            using var verified = VerifiedPayloadLock.OpenAndVerify(path, expected);
            Assert.Equal(Path.GetFullPath(path), verified.Path);
            Assert.Equal(expected, verified.Sha256);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void VerifiedPayloadLockPreventsWriteAndRenameUntilTheConsumerReleasesIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "sc-installer-held-payload-" + Guid.NewGuid().ToString("N"));
        var renamedDirectory = directory + ".replacement";
        var path = Path.Combine(directory, "payload.bin");
        var renamedPayload = path + ".replacement";
        var bytes = new byte[] { 0x41, 0x42, 0x43 };
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, bytes);
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            var held = VerifiedPayloadLock.OpenAndVerify(path, expected);
            try
            {
                using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    Assert.Equal(bytes[0], (byte)reader.ReadByte());
                    Assert.Equal(bytes[1], (byte)reader.ReadByte());
                    Assert.Equal(bytes[2], (byte)reader.ReadByte());
                }
                Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
                Assert.Throws<IOException>(() => File.Move(path, renamedPayload));
                Assert.Throws<IOException>(() => Directory.Move(directory, renamedDirectory));
                Assert.True(File.Exists(path));
            }
            finally
            {
                held.Dispose();
            }

            using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                writer.WriteByte(0x55);
            }
        }
        finally
        {
            foreach (var candidate in new[] { directory, renamedDirectory })
            {
                var candidateFile = Path.Combine(candidate, "payload.bin");
                if (File.Exists(candidateFile)) File.Delete(candidateFile);
                if (File.Exists(candidateFile + ".replacement")) File.Delete(candidateFile + ".replacement");
                if (Directory.Exists(candidate)) Directory.Delete(candidate);
            }
        }
    }

    [Fact]
    public void ExactHklmRunValueNameUsesCaseInsensitiveRegistryComparison()
    {
        Assert.True(InstallerAuthorizationProtocol.ContainsRegistryValueName(
            ExistingRunValueNames, "StorageChronicleSessionAgent"));
        Assert.False(InstallerAuthorizationProtocol.ContainsRegistryValueName(
            MissingRunValueNames, "StorageChronicleSessionAgent"));
        Assert.Throws<ArgumentNullException>(() => InstallerAuthorizationProtocol.ContainsRegistryValueName(null!, "StorageChronicleSessionAgent"));
    }

    [Fact]
    public void TimedOutChildRemainsIndeterminateUntilItsExactHandleProvesExit()
    {
        var state = new InstallerChildCompletionState();

        Assert.False(state.TryProveTerminal(false, 0));
        state.MarkTimedOut();
        Assert.True(state.MustRemainIndeterminate);
        Assert.False(state.TerminalProven);
        Assert.False(state.TryProveTerminal(false, 124));
        Assert.False(state.TerminalProven);
        Assert.True(state.TryProveTerminal(true, 0));
        Assert.True(state.TerminalProven);
        Assert.True(state.MustRemainIndeterminate);
        Assert.Equal(0, state.ExitCode);
    }

    [Fact]
    public void InstallerHarnessPinsPayloadHandlesAndFailsClosedOnUnsafeDriverTimeoutCode()
    {
        var root = FindRoot();
        var harness = File.ReadAllText(Path.Combine(root, "build", "package", "Test-Installer.ps1"));
        var driver = File.ReadAllText(Path.Combine(root, "tools", "PhysicalAcceptance", "Invoke-RealInstallerCase.ps1"));
        var runCheck = harness.IndexOf("Assert-HklmRunValueNameAbsent -ValueName 'StorageChronicleSessionAgent'", StringComparison.Ordinal);
        var payloadLock = harness.IndexOf("VerifiedPayloadLock]::OpenAndVerify($candidatePath", StringComparison.Ordinal);
        var driverGuard = harness.IndexOf("Assert-BundledDriverTimeoutContract -Path $DriverScript", StringComparison.Ordinal);
        var launch = harness.IndexOf("Start-UacInstallerCase -FilePath", StringComparison.Ordinal);
        var physicalCaseStart = harness.IndexOf("function Invoke-PhysicalInstallerCase", StringComparison.Ordinal);
        var physicalCaseEnd = harness.IndexOf("function Invoke-InstallerCase", physicalCaseStart, StringComparison.Ordinal);
        var physicalCase = harness.Substring(physicalCaseStart, physicalCaseEnd - physicalCaseStart);
        var justBeforeFirstLaunchCheck = physicalCase.IndexOf("if (-not $script:HasLaunchedFirstPrivilegedChild)", StringComparison.Ordinal);
        var physicalLaunch = physicalCase.IndexOf("Start-UacInstallerCase -FilePath", StringComparison.Ordinal);

        Assert.True(runCheck >= 0 && runCheck < launch);
        Assert.True(payloadLock >= 0 && payloadLock < launch);
        Assert.True(driverGuard >= 0 && driverGuard < launch);
        Assert.True(physicalCaseStart >= 0 && physicalCaseEnd > physicalCaseStart && justBeforeFirstLaunchCheck >= 0 && justBeforeFirstLaunchCheck < physicalLaunch);
        var authorization = File.ReadAllText(Path.Combine(root, "build", "package", "InstallerAuthorizationProtocol.cs"));
        Assert.Contains("FileShareRead", authorization, StringComparison.Ordinal);
        Assert.Contains("FileFlagOpenReparsePoint", authorization, StringComparison.Ordinal);
        Assert.Contains("AcquireParentDirectoryHandles", authorization, StringComparison.Ordinal);
        Assert.Contains("ParseInput($source", harness, StringComparison.Ordinal);
        Assert.Contains("RegistryView]::Registry64", harness, StringComparison.Ordinal);
        Assert.Contains("RegistryView]::Registry32", harness, StringComparison.Ordinal);
        Assert.Contains("ChildTerminatedByHarness = $false", harness, StringComparison.Ordinal);
        Assert.Contains("$Process.WaitForExit()", harness, StringComparison.Ordinal);
        Assert.Contains("TryProveTerminal($Process.HasExited, $Process.ExitCode)", harness, StringComparison.Ordinal);
        Assert.Contains("Status 'INDETERMINATE'", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("$process.Kill(", harness, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get-FileHash", harness, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dispose-VerifiedPayloadLocks", harness, StringComparison.Ordinal);
        Assert.Contains("if ($script:StopAfterLiveChild)", harness, StringComparison.Ordinal);

        var invokeCapturedStart = driver.IndexOf("function Invoke-Captured", StringComparison.Ordinal);
        var invokeCapturedEnd = driver.IndexOf("function Invoke-Msi", invokeCapturedStart, StringComparison.Ordinal);
        Assert.True(invokeCapturedStart >= 0 && invokeCapturedEnd > invokeCapturedStart);
        var capturedDriver = driver.Substring(invokeCapturedStart, invokeCapturedEnd - invokeCapturedStart);
        Assert.Contains("$timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)", capturedDriver, StringComparison.Ordinal);
        Assert.Contains("$process.WaitForExit()", capturedDriver, StringComparison.Ordinal);
        Assert.Contains("TimedOut = $timedOut", capturedDriver, StringComparison.Ordinal);
        Assert.DoesNotContain("$process.Kill(", capturedDriver, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no dependent operation may proceed", driver, StringComparison.Ordinal);
        Assert.Equal(5, driver.Split("Assert-CapturedCompletedWithinDeadline -Captured", StringSplitOptions.None).Length - 1);
        Assert.Contains("function Assert-CapturedCompletedWithinDeadline", driver, StringComparison.Ordinal);
        Assert.Contains("physical execution is refused before UAC", harness, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerBrokerPipeNameCollisionFailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pipeName = "SCInstallerCollision-" + InstallerAuthorizationProtocol.CreateToken();
        using var first = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.None);
        Assert.Throws<IOException>(() => new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.None));
    }

    [Fact]
    public void PhysicalInstallerBrokerOrdersDisclosureConsentPipeIdentityGrantAndPostExitReads()
    {
        var root = FindRoot();
        var harness = File.ReadAllText(Path.Combine(root, "build", "package", "Test-Installer.ps1"));
        var disclose = harness.IndexOf("Write-Host $disclosure", StringComparison.Ordinal);
        var parentConsent = harness.IndexOf("Read-Host \"Type exactly", StringComparison.Ordinal);
        var pipe = harness.IndexOf("New-CurrentUserInstallerPipe -PipeName", StringComparison.Ordinal);
        var uac = harness.IndexOf("Start-UacInstallerCase -FilePath", StringComparison.Ordinal);
        var clientPid = harness.IndexOf("GetClientProcessId($pipe)", StringComparison.Ordinal);
        var helloCheck = harness.IndexOf("ValidateHello(", StringComparison.Ordinal);
        var grant = harness.IndexOf("Schema = 'StorageChronicle.InstallerCaseAuthorizationGrant.v1'", StringComparison.Ordinal);
        var exitWait = harness.IndexOf("$process.WaitForExit($CaseTimeoutSeconds * 1000)", StringComparison.Ordinal);
        var resultRead = harness.IndexOf("Read-PhysicalDriverLog -LogPath", StringComparison.Ordinal);

        Assert.True(disclose >= 0 && disclose < parentConsent && parentConsent < pipe && pipe < uac && uac < clientPid && clientPid < helloCheck && helloCheck < grant && grant < exitWait && exitWait < resultRead);
        Assert.Contains("NamedPipeServerStreamAcl]::Create", harness, StringComparison.Ordinal);
        Assert.Contains("PipeAccessRights]0", harness, StringComparison.Ordinal);
        Assert.Contains("SetAccessRuleProtection($true, $false)", harness, StringComparison.Ordinal);
        Assert.Contains("ConsentPromptBehaviorAdmin", harness, StringComparison.Ordinal);
        Assert.Contains("$physicalIsElevated", harness, StringComparison.Ordinal);
        Assert.Contains("$isAdminGroupMember", harness, StringComparison.Ordinal);
        Assert.Contains("$promptBehavior -notin @(1, 2, 3, 4)", harness, StringComparison.Ordinal);
        Assert.Contains("$startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden", harness, StringComparison.Ordinal);
        Assert.Contains("$startInfo.Verb = 'runas'", harness, StringComparison.Ordinal);
        Assert.Contains("AuthorizationParentProcessId', [string]$PID", harness, StringComparison.Ordinal);
        Assert.Contains("ExpectedHashManifestSha256', $ExpectedHashManifestSha256", harness, StringComparison.Ordinal);
        var invocation = harness.Substring(harness.IndexOf("function Get-DriverInvocation", StringComparison.Ordinal), harness.IndexOf("function Read-DriverResult", StringComparison.Ordinal) - harness.IndexOf("function Get-DriverInvocation", StringComparison.Ordinal));
        Assert.Contains("'-AuthorizationPipeName', $script:AuthorizationPipeName", invocation, StringComparison.Ordinal);
        Assert.Contains("'-AuthorizationNonce', $script:AuthorizationNonce", invocation, StringComparison.Ordinal);
        Assert.DoesNotContain("'-CaseAuthorizationPhrase'", invocation, StringComparison.Ordinal);
        Assert.DoesNotContain("PipeOptions]::CurrentUserOnly", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("StandardInput", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("STORAGE_CHRONICLE_INSTALLER_AUTHORIZATION_NONCE", harness, StringComparison.Ordinal);
        Assert.Contains("$script:StopAfterLiveChild = $true", harness, StringComparison.Ordinal);
        var protocol = File.ReadAllText(Path.Combine(root, "build", "package", "InstallerAuthorizationProtocol.cs"));
        Assert.Contains("StorageChronicle.InstallerCaseAuthorizationHello.v1", protocol, StringComparison.Ordinal);
        Assert.Contains("StorageChronicle.InstallerCaseAuthorizationGrant.v1", harness, StringComparison.Ordinal);
        Assert.Contains("ComputerName = $env:COMPUTERNAME", harness, StringComparison.Ordinal);
        Assert.Contains("CaseAuthorizationPhrase = $script:CaseAuthorizationPhrase", harness, StringComparison.Ordinal);
        Assert.Contains("StorageChronicle.InstallerCaseResult.v1", harness, StringComparison.Ordinal);
        Assert.Contains("StorageChronicle.InstallerCaseLog.v1", harness, StringComparison.Ordinal);
        Assert.Contains("Id = 'service'; Name = 'LocalSystem service and recovery'; Requirements = @('MsiPath', 'ServiceName')", harness, StringComparison.Ordinal);
        Assert.Contains("'session' { return", harness, StringComparison.Ordinal);
        Assert.Contains("'non-admin' { return", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("'session-agent' { return", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("'non-admin-ui' { return", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("Add-Precondition 'ServiceCredentialReference'", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceCredentialReference", harness, StringComparison.Ordinal);
        Assert.Contains("Add-Precondition 'UacEnabled'", harness, StringComparison.Ordinal);
        var launcher = File.ReadAllText(Path.Combine(root, "tools", "PhysicalAcceptance", "Run-RealMachineInstallerAcceptance.ps1"));
        Assert.Contains("ConsentPromptBehaviorAdmin", launcher, StringComparison.Ordinal);
        Assert.Contains("$isAdminGroupMember", launcher, StringComparison.Ordinal);
        Assert.Contains("$adminPromptBehavior -in @(1, 2, 3, 4)", launcher, StringComparison.Ordinal);
        Assert.Contains("-not $uacPromptConfigured", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestContainsSingleProductAndRecoveryContract()
    {
        var root = FindRoot();
        var wix = File.ReadAllText(Path.Combine(root, "installer", "StorageChronicle.wxs"));
        Assert.Contains("Storage Chronicle", wix, StringComparison.Ordinal);
        Assert.Contains("Account=\"LocalSystem\"", wix, StringComparison.Ordinal);
        Assert.Contains("ServiceConfig", wix, StringComparison.Ordinal);
        Assert.Contains("RestartServiceDelayInSeconds=\"5\"", wix, StringComparison.Ordinal);
        Assert.Contains("ConfigureAgentServiceRecovery", wix, StringComparison.Ordinal);
        Assert.Contains("After=\"StartServices\"", wix, StringComparison.Ordinal);
        var recovery = File.ReadAllText(Path.Combine(root, "src", "StorageChronicle.Agent", "WindowsServiceRecoveryConfigurator.cs"));
        Assert.Contains("5_000", recovery, StringComparison.Ordinal);
        Assert.Contains("15_000", recovery, StringComparison.Ordinal);
        Assert.Contains("60_000", recovery, StringComparison.Ordinal);
        Assert.Contains("GetExecutablePath", recovery, StringComparison.Ordinal);
        var program = File.ReadAllText(Path.Combine(root, "src", "StorageChronicle.Agent", "Program.cs"));
        Assert.Contains("--configure-service-recovery", program, StringComparison.Ordinal);
        Assert.Contains("CurrentVersion\\Run", wix, StringComparison.Ordinal);
        Assert.Contains("CommonAppDataFolder", wix, StringComparison.Ordinal);
        Assert.Contains("Permanent=\"yes\"", wix, StringComparison.Ordinal);
        Assert.DoesNotContain("Driver", wix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VirtualBoxInstallerDriverExistsButPhysicalPolicyDisablesHarnessVmExecution()
    {
        var root = FindRoot();
        var genericHarness = File.ReadAllText(Path.Combine(root, "build", "package", "Test-Installer.ps1"));
        var driver = File.ReadAllText(Path.Combine(root, "tools", "PhysicalAcceptance", "Invoke-VirtualBoxInstallerCase.ps1"));
        var orchestrator = File.ReadAllText(Path.Combine(root, "tools", "TestEnvironment", "Run-VirtualBoxInstallerAcceptance.ps1"));

        Assert.Contains("GuestCredentialReference", genericHarness, StringComparison.Ordinal);
        Assert.Contains("$validTargetKind = $TargetKind -eq 'PhysicalMachine'", genericHarness, StringComparison.Ordinal);
        Assert.Contains("$validMode = $ExecutionMode -eq 'Local'", genericHarness, StringComparison.Ordinal);
        Assert.Contains("VM and non-physical installer execution are disabled", genericHarness, StringComparison.Ordinal);
        Assert.Contains("Invoke-VBoxGuestControl", driver, StringComparison.Ordinal);
        Assert.Contains("Copy-TestArtifactToVm", driver, StringComparison.Ordinal);
        Assert.Contains("Copy-TestArtifactFromVm", driver, StringComparison.Ordinal);
        Assert.Contains("Status = 'FAILED'", driver, StringComparison.Ordinal);
        Assert.Contains("SC-CLEAN-BASELINE", orchestrator, StringComparison.Ordinal);
        Assert.Contains("-Apply", orchestrator, StringComparison.Ordinal);
        Assert.Contains("AcceptanceEligible", orchestrator, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows10AcceptanceUsesPhysicalEvidenceAndRejectsLegacyVmComposition()
    {
        var root = FindRoot();
        var stageA = File.ReadAllText(Path.Combine(root, "tools", "TestEnvironment", "Compose-Windows10StageA.ps1"));
        var physical = File.ReadAllText(Path.Combine(root, "tools", "PhysicalAcceptance", "Finalize-Windows10PhysicalAcceptance.ps1"));

        Assert.Contains("StorageChronicle.Windows10StageAAcceptance.v1", stageA, StringComparison.Ordinal);
        Assert.Contains("COMPLETED_REAL_IO_ACCEPTANCE", stageA, StringComparison.Ordinal);
        Assert.Contains("StorageChronicle.Windows10StageACheck.v1", stageA, StringComparison.Ordinal);
        Assert.Contains("EvidenceOrigin -ne 'real'", stageA, StringComparison.Ordinal);
        Assert.Contains("StorageChronicle.Windows10PhysicalAcceptance.v1", physical, StringComparison.Ordinal);
        Assert.Contains("Status = 'NOT_EXECUTED'", physical, StringComparison.Ordinal);
        Assert.Contains("Windows10PrivilegedManifestPath", physical, StringComparison.Ordinal);
        Assert.Contains("StorageChronicle.WindowsPrivilegedAcceptance.v2", physical, StringComparison.Ordinal);
        Assert.Contains("Assert-PhysicalCapabilities", physical, StringComparison.Ordinal);
        Assert.Contains("not from the same physical computer", physical, StringComparison.Ordinal);
        Assert.DoesNotContain("Assert-StageA", physical, StringComparison.Ordinal);
        Assert.DoesNotContain("VirtualBoxVm", physical, StringComparison.Ordinal);
        Assert.Contains("ComputerName = $env:COMPUTERNAME", File.ReadAllText(Path.Combine(root, "build", "Test-Privileged.ps1")), StringComparison.Ordinal);
        Assert.Contains("ComputerName = $env:COMPUTERNAME", File.ReadAllText(Path.Combine(root, "build", "package", "Test-Installer.ps1")), StringComparison.Ordinal);
    }

    [Fact]
    public void Windows10CapabilityChecksAreGuestRealAndFailClosed()
    {
        var root = FindRoot();
        var guest = File.ReadAllText(Path.Combine(root, "tools", "TestEnvironment", "Test-Windows10StageACapability.ps1"));
        var host = File.ReadAllText(Path.Combine(root, "tools", "TestEnvironment", "Invoke-Windows10StageACapabilityChecks.ps1"));

        Assert.Contains("StorageChronicle.Windows10StageACheck.v1", guest, StringComparison.Ordinal);
        Assert.Contains("LoadLibrary/GetProcAddress", guest, StringComparison.Ordinal);
        Assert.Contains("cldapi.dll", guest, StringComparison.Ordinal);
        Assert.Contains("pnputil.exe", guest, StringComparison.Ordinal);
        Assert.Contains("DriverPresent", guest, StringComparison.Ordinal);
        Assert.Contains("ApiCalled = $false", guest, StringComparison.Ordinal);
        Assert.Contains("SC-Test-W10-VBox", host, StringComparison.Ordinal);
        Assert.Contains("Assert-VirtualBoxHostPrerequisites", host, StringComparison.Ordinal);
        Assert.Contains("if (-not $Apply)", host, StringComparison.Ordinal);
        Assert.Contains("AcceptanceEligible = $false", host, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivilegedAcceptanceUsesOneFixedCapabilityContract()
    {
        var root = FindRoot();
        var contract = File.ReadAllText(Path.Combine(root, "build", "quality", "AcceptanceContracts.ps1"));
        var producer = File.ReadAllText(Path.Combine(root, "build", "Test-Privileged.ps1"));
        var wrapper = File.ReadAllText(Path.Combine(root, "build", "Test-WindowsPrivileged.ps1"));
        var finalGate = File.ReadAllText(Path.Combine(root, "build", "quality", "Test-FinalAcceptance.ps1"));

        foreach (var capability in new[] { "Vhdx", "UsnQuery", "UsnRead", "Mft", "Reconciliation", "Etw", "ReadDirectoryChangesW", "BufferGap", "Smb", "Service", "SessionAgent", "Clipboard", "VolumeGuid", "HotAttachDetach", "AclDeniedMetadata", "NonNtfs" })
        {
            Assert.Contains($"'{capability}'", contract, StringComparison.Ordinal);
        }

        Assert.Contains("AcceptanceContracts.ps1", producer, StringComparison.Ordinal);
        Assert.Contains("Get-RequiredWindowsPrivilegedCapabilities", producer, StringComparison.Ordinal);
        Assert.Contains("Get-RequiredWindowsPrivilegedCapabilities", finalGate, StringComparison.Ordinal);
        Assert.Contains("Windows 10 privileged evidence", finalGate, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows10StageAAcceptance.v1", finalGate, StringComparison.Ordinal);
        Assert.Contains("WorkloadOraclePath", wrapper, StringComparison.Ordinal);
        Assert.Contains("AgentHistoryPath", wrapper, StringComparison.Ordinal);
        Assert.Contains("Windows 11", finalGate, StringComparison.Ordinal);
        Assert.Contains("IsAdministrator", finalGate, StringComparison.Ordinal);
        Assert.Contains("AcceptanceEligible=true", finalGate, StringComparison.Ordinal);
        Assert.Contains("placeholder, failed, or ineligible result", finalGate, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalVhdxRunnerRevalidatesDiskRolesAndNeverDeletesItsFixture()
    {
        var root = FindRoot();
        var producer = File.ReadAllText(Path.Combine(root, "build", "Test-Privileged.ps1"));
        var beforeInitialize = producer.Split("Initialize-Disk -UniqueId $diskUniqueId", StringSplitOptions.None)[0];

        Assert.Contains("Get-Disk -UniqueId $uniqueId", producer, StringComparison.Ordinal);
        Assert.Contains("$resolvedImageDisks[0].Number -ne [uint32]$disk.Number", producer, StringComparison.Ordinal);
        Assert.Contains("Assert-VhdxDiskHasNoPagingOrCrashDumpRole $diskUniqueId", beforeInitialize, StringComparison.Ordinal);
        Assert.Contains("Dismount-DiskImage -ImagePath $VhdxPath -ErrorAction Stop", producer, StringComparison.Ordinal);
        Assert.Contains("DETACHED_VERIFIED", producer, StringComparison.Ordinal);
        Assert.Contains("VhdxDetachStatus = 'FAILED'", producer, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $VhdxPath", producer, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivilegedRunnerRejectsPopulatedFixtureRootsAndBindsMarkersToLiveVolume()
    {
        var root = FindRoot();
        var producer = File.ReadAllText(Path.Combine(root, "build", "Test-Privileged.ps1"));
        var markerValidation = producer.IndexOf("function Assert-TestLabMarker", StringComparison.Ordinal);
        var markerRead = producer.IndexOf("Get-Content -Raw -Encoding UTF8 -LiteralPath $path | ConvertFrom-Json", markerValidation, StringComparison.Ordinal);
        var unknownEntryGuard = producer.IndexOf("Fixture root is not fresh/run-owned", markerValidation, StringComparison.Ordinal);
        var volumeIdentityGuard = producer.IndexOf("marker volume identity does not match the volume resolved from the fixture root", markerValidation, StringComparison.Ordinal);

        Assert.True(markerValidation >= 0 && markerRead > markerValidation);
        Assert.True(unknownEntryGuard > markerValidation && unknownEntryGuard < markerRead);
        Assert.True(volumeIdentityGuard > markerRead);
        Assert.Contains("DeviceID='$escapedDeviceId'", producer, StringComparison.Ordinal);
        Assert.Contains("A local drive or verified volume GUID path is required", producer, StringComparison.Ordinal);
        Assert.Contains("$fullPath.StartsWith($item + '\\'", producer, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivilegedEvidenceRootRejectsWindowsAndApplicationDataTrees()
    {
        var root = FindRoot();
        var producer = File.ReadAllText(Path.Combine(root, "build", "Test-Privileged.ps1"));
        var guard = producer.IndexOf("function Assert-OutsideProtectedSystemRoots", StringComparison.Ordinal);
        var evidenceCheck = producer.IndexOf("Assert-OutsideProtectedSystemRoots $artifactRoot", StringComparison.Ordinal);

        Assert.True(guard >= 0 && evidenceCheck > guard);
        Assert.Contains("$env:WINDIR", producer, StringComparison.Ordinal);
        Assert.Contains("$env:ProgramFiles", producer, StringComparison.Ordinal);
        Assert.Contains("${env:ProgramFiles(x86)}", producer, StringComparison.Ordinal);
        Assert.Contains("$env:ProgramData", producer, StringComparison.Ordinal);
        Assert.Contains("EvidenceRoot must not be inside a protected Windows/application data root", producer, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalInstallerMutationRequiresFreshRunOwnershipAndCannotFallBackToVmOrOverwriteEvidence()
    {
        var root = FindRoot();
        var launcher = File.ReadAllText(Path.Combine(root, "tools", "PhysicalAcceptance", "Run-RealMachineInstallerAcceptance.ps1"));
        var harness = File.ReadAllText(Path.Combine(root, "build", "package", "Test-Installer.ps1"));
        var driver = File.ReadAllText(Path.Combine(root, "tools", "PhysicalAcceptance", "Invoke-RealInstallerCase.ps1"));
        var probe = File.ReadAllText(Path.Combine(root, "tools", "PhysicalAcceptance", "Probe-WriteAccess.ps1"));

        Assert.DoesNotContain("SkipConfirmation", launcher, StringComparison.Ordinal);
        Assert.Contains("ExpectedHashManifestSha256", launcher, StringComparison.Ordinal);
        Assert.Contains("EvidenceRoot must be a new path", launcher, StringComparison.Ordinal);
        Assert.Contains("I CONFIRM DEDICATED PC", harness, StringComparison.Ordinal);
        Assert.Contains("StorageChronicle.PhysicalInstallerOwnerReceipt.v1", harness, StringComparison.Ordinal);
        Assert.Contains("VM and non-physical installer execution are disabled", harness, StringComparison.Ordinal);
        Assert.Contains("Bundle hash entry is unsafe or malformed", harness, StringComparison.Ordinal);
        Assert.Contains("$relativePath -match '(^|[\\\\/])\\.\\.([\\\\/]|$)'", harness, StringComparison.Ordinal);
        Assert.Contains("Physical installer input must be the exact fingerprinted bundle payload", harness, StringComparison.Ordinal);
        Assert.Contains("Bundle hash manifest contains a duplicate payload path", harness, StringComparison.Ordinal);
        Assert.Contains("The volume marker schema, role, label, filesystem, or TestId", launcher, StringComparison.Ordinal);
        Assert.Contains("must be the canonical Storage Chronicle directory under Program Files", harness, StringComparison.Ordinal);
        Assert.Contains("must be the canonical Storage Chronicle history directory under ProgramData", harness, StringComparison.Ordinal);
        Assert.Contains("outside system/application roots, repository, bundle, OneDrive, and Documents", harness, StringComparison.Ordinal);
        Assert.Contains("Installer driver paths must be the canonical product install/history paths", driver, StringComparison.Ordinal);
        Assert.Contains("NTFS fixed-volume role", driver, StringComparison.Ordinal);
        Assert.Contains("Write-NewUtf8File $manifestPath $json", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Content -LiteralPath $manifestPath", harness, StringComparison.Ordinal);
        Assert.Contains("AuthorizationNonce", driver, StringComparison.Ordinal);
        Assert.Contains("CaseAuthorizationPhrase", driver, StringComparison.Ordinal);
        Assert.Contains("I AUTHORIZE STORAGE CHRONICLE CASE $CaseId ON $env:COMPUTERNAME RUN $RunId", driver, StringComparison.Ordinal);
        Assert.Contains("$caseAnswer = Read-Host", harness, StringComparison.Ordinal);
        Assert.Contains("$grant.CaseAuthorizationPhrase -cne $expectedCaseAuthorization", driver, StringComparison.Ordinal);
        Assert.DoesNotContain("[Console]::ReadLine()", driver, StringComparison.Ordinal);
        Assert.DoesNotContain("-CaseAuthorizationPhrase", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("RedirectStandardInput", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("-StandardInput", harness, StringComparison.Ordinal);
        Assert.Contains("CaseAuthorizationPhrase = $script:CaseAuthorizationPhrase", harness, StringComparison.Ordinal);
        Assert.Contains("StorageChronicle.InstallerCaseAuthorizationGrant.v1", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("STORAGE_CHRONICLE_INSTALLER_AUTHORIZATION_NONCE", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("'-NonInteractive'", harness, StringComparison.Ordinal);
        Assert.True(driver.IndexOf("$grant.CaseAuthorizationPhrase -cne $expectedCaseAuthorization", StringComparison.Ordinal) < driver.IndexOf("switch ($CaseId)", StringComparison.Ordinal));
        Assert.Contains("HumanConfirmation", driver, StringComparison.Ordinal);
        Assert.Contains("FileMode]::CreateNew", driver, StringComparison.Ordinal);
        Assert.DoesNotContain("New-Item -ItemType Directory -Force -Path $permissionRoot", driver, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $permissionRoot -Recurse", driver, StringComparison.Ordinal);
        Assert.DoesNotContain("catch { exit 0 }", probe, StringComparison.Ordinal);
        Assert.Contains("$probeExitCode = 2", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void RetiredTestLabAndAgentModeFailClosedBeforeSideEffects()
    {
        var root = FindRoot();
        var agent = File.ReadAllText(Path.Combine(root, "src", "StorageChronicle.Agent", "Program.cs"));
        var testLab = File.ReadAllText(Path.Combine(root, "tools", "TestEnvironment", "Invoke-WindowsTestLab.ps1"));

        Assert.Contains("--testlab is retired", agent, StringComparison.Ordinal);
        Assert.Contains("return 2;", agent, StringComparison.Ordinal);
        Assert.Contains("This VM/guest TestLab runner is retired", testLab, StringComparison.Ordinal);
        Assert.Contains("exit 2", testLab, StringComparison.Ordinal);
        Assert.DoesNotContain("TestLab.Common.ps1", testLab, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-GuestCommand", testLab, StringComparison.Ordinal);
        Assert.DoesNotContain("Initialize-Disk", testLab, StringComparison.Ordinal);
        Assert.DoesNotContain("Format-Volume", testLab, StringComparison.Ordinal);
    }

    [Fact]
    public void RetiredVirtualBoxDataDiskHelperHasNoOperationalDependencies()
    {
        var root = FindRoot();
        var helper = File.ReadAllText(Path.Combine(root, "tools", "TestEnvironment", "New-TestDataVhdx.ps1"));

        Assert.Contains("This legacy VirtualBox VDI creation/attachment helper is retired", helper, StringComparison.Ordinal);
        Assert.Contains("performs no host, VM, disk, file, or evidence operations", helper, StringComparison.Ordinal);
        Assert.Contains("exit 2", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("TestLab.Common.ps1", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-VBoxManage", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("New-Item", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-TestLabJson", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalAcceptanceCorrelationRequiresLivePhysicalRowsAndArtifacts()
    {
        var root = FindRoot();
        var finalGate = File.ReadAllText(Path.Combine(root, "build", "quality", "Test-FinalAcceptance.ps1"));
        var wrapper = File.ReadAllText(Path.Combine(root, "build", "quality", "Test-CorrelationMetrics.ps1"));

        Assert.Contains("TargetKind -ne 'PhysicalMachine'", finalGate, StringComparison.Ordinal);
        Assert.Contains("ExecutionMode -ne 'Local'", finalGate, StringComparison.Ordinal);
        Assert.Contains("AgentHostMode -ne 'Service'", finalGate, StringComparison.Ordinal);
        Assert.Contains("AgentHistoryPath", finalGate, StringComparison.Ordinal);
        Assert.Contains("WorkloadExecutablePath", finalGate, StringComparison.Ordinal);
        Assert.Contains("FalseAttributionCount", finalGate, StringComparison.Ordinal);
        Assert.Contains("DroppedEventCount", finalGate, StringComparison.Ordinal);
        Assert.Contains("ProcessAttribution.Rows", finalGate, StringComparison.Ordinal);
        Assert.Contains("ExplorerSourceCorrelation.Rows", finalGate, StringComparison.Ordinal);
        Assert.Contains("AgentHistoryPath", wrapper, StringComparison.Ordinal);
        Assert.Contains("SourceCorrelatedCount", wrapper, StringComparison.Ordinal);
        Assert.Contains("FileStateCorrectness", wrapper, StringComparison.Ordinal);
        Assert.Contains("SourceEventCount", finalGate, StringComparison.Ordinal);
        Assert.Contains("FailureReasons", finalGate, StringComparison.Ordinal);
        Assert.DoesNotContain("VirtualBoxInstallerAcceptance", finalGate, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsTestLabExecution", finalGate, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalAcceptanceRequiresCurrentPhysicalSafetyAuditAndRejectsVmEvidence()
    {
        var root = FindRoot();
        var finalGate = File.ReadAllText(Path.Combine(root, "build", "quality", "Test-FinalAcceptance.ps1"));

        Assert.Contains("PhysicalReadOnlyAuditManifest", finalGate, StringComparison.Ordinal);
        Assert.Contains("Name = 'PhysicalSafety'", finalGate, StringComparison.Ordinal);
        Assert.Contains("rev-parse HEAD", finalGate, StringComparison.Ordinal);
        Assert.Contains("status --porcelain", finalGate, StringComparison.Ordinal);
        Assert.Contains("independentWriteMonitoring", finalGate, StringComparison.Ordinal);
        Assert.Contains("overallStatus -ne 'PASS'", finalGate, StringComparison.Ordinal);
        Assert.Contains("Environment.RunId -notmatch", finalGate, StringComparison.Ordinal);
        Assert.Contains("Environment.ComputerName", finalGate, StringComparison.Ordinal);
        Assert.Contains("Physical safety audit does not declare AcceptanceEligible=true", finalGate, StringComparison.Ordinal);
        Assert.Contains("Validation failed: $($_.Exception.Message)", finalGate, StringComparison.Ordinal);
        Assert.Contains("Write-NewJsonArtifact -Path $OutputPath -Value $evidence", finalGate, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Content -LiteralPath $OutputPath", finalGate, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows11VirtualBoxInstallerManifest", finalGate, StringComparison.Ordinal);
        Assert.DoesNotContain("TestLabAndRealIo", finalGate, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalAcceptanceCollectorCreatesEvidenceWithoutReplacingExistingFiles()
    {
        var root = FindRoot();
        var collector = File.ReadAllText(Path.Combine(root, "tools", "PhysicalAcceptance", "Collect-PhysicalAcceptanceResults.ps1"));

        Assert.Contains("Write-NewJsonArtifact -Path $OutputPath -Value $payload", collector, StringComparison.Ordinal);
        Assert.Contains("AcceptanceContracts.ps1", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Content -LiteralPath $OutputPath", collector, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptanceEvidenceWritersShareAtomicCreateOnlyJsonContract()
    {
        var root = FindRoot();
        var contracts = File.ReadAllText(Path.Combine(root, "build", "quality", "AcceptanceContracts.ps1"));
        var writers = new[]
        {
            "build/quality/Test-FinalAcceptance.ps1",
            "build/quality/Test-BranchIntegration.ps1",
            "build/quality/New-ResourceQuietWitness.ps1",
            "build/quality/Test-CorrelationMetrics.ps1",
            "build/quality/Test-ResourceBudget.ps1",
            "tools/PhysicalAcceptance/Collect-PhysicalAcceptanceResults.ps1",
            "tools/PhysicalAcceptance/Finalize-Windows10PhysicalAcceptance.ps1",
            "tools/TestEnvironment/Compose-Windows10StageA.ps1"
        };

        Assert.Contains("function Write-NewJsonArtifact", contracts, StringComparison.Ordinal);
        Assert.Contains("function Write-NewTextArtifact", contracts, StringComparison.Ordinal);
        Assert.Contains("function Write-NewArtifactBytes", contracts, StringComparison.Ordinal);
        Assert.Contains("The JSON artifact destination already exists", contracts, StringComparison.Ordinal);
        Assert.Contains("[System.IO.FileMode]::CreateNew", contracts, StringComparison.Ordinal);
        Assert.Contains("$stream.Flush($true)", contracts, StringComparison.Ordinal);
        Assert.Contains("[System.IO.File]::Move($temporaryPath, $fullPath)", contracts, StringComparison.Ordinal);
        foreach (var writer in writers)
        {
            var source = File.ReadAllText(Path.Combine(root, writer.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Contains("Write-NewJsonArtifact", source, StringComparison.Ordinal);
        }

        var correlation = File.ReadAllText(Path.Combine(root, "build", "quality", "Test-CorrelationMetrics.ps1"));
        var correlationProducer = File.ReadAllText(Path.Combine(root, "tests", "StorageChronicle.CorrelationAcceptance.Tests", "CorrelationMetricsAcceptanceTests.cs"));
        var liveValidator = File.ReadAllText(Path.Combine(root, "tools", "StorageChronicle.LiveCorrelationValidator", "Program.cs"));
        Assert.Contains("Write-NewTextArtifact", correlation, StringComparison.Ordinal);
        Assert.Contains("rawReportPath", correlation, StringComparison.Ordinal);
        Assert.DoesNotContain("SC-Test-W11-VBox", correlation, StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", correlationProducer, StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", liveValidator, StringComparison.Ordinal);
        Assert.DoesNotContain("SC-Test-W11-VBox", liveValidator, StringComparison.Ordinal);

        var resourceAcceptance = File.ReadAllText(Path.Combine(root, "build", "quality", "Test-ResourceBudgetAcceptance.ps1"));
        var resourceBudget = File.ReadAllText(Path.Combine(root, "build", "quality", "Test-ResourceBudget.ps1"));
        var resourceMonitor = File.ReadAllText(Path.Combine(root, "tools", "StorageChronicle.ResourceMonitor", "Program.cs"));
        Assert.Contains("Write-NewTextArtifact -Path $stdoutPath", resourceAcceptance, StringComparison.Ordinal);
        Assert.Contains("Write-NewJsonArtifact -Path $evidencePath", resourceAcceptance, StringComparison.Ordinal);
        Assert.Contains("Write-NewJsonArtifact -Path $evidencePath -Value $fallback", resourceAcceptance, StringComparison.Ordinal);
        Assert.Contains("[guid]::NewGuid().ToString('N')", resourceAcceptance, StringComparison.Ordinal);
        Assert.Contains("[guid]::NewGuid().ToString('N')", resourceBudget, StringComparison.Ordinal);
        Assert.Contains("$processIds = @($ProcessId -split '[,;]'", resourceBudget, StringComparison.Ordinal);
        Assert.Contains("'--output', $rawOutput", resourceBudget, StringComparison.Ordinal);
        Assert.Contains("Write-NewJsonArtifact -Path $output -Value $resource", resourceBudget, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Content", resourceBudget, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FileMode.CreateNew", resourceMonitor, StringComparison.Ordinal);
        Assert.Contains("stream.Flush(flushToDisk: true)", resourceMonitor, StringComparison.Ordinal);
        Assert.Contains("Existing resource evidence is preserved", resourceMonitor, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllText", resourceMonitor, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows10ManualBundleCarriesSharedAcceptanceContract()
    {
        var root = FindRoot();
        var bundleGenerator = File.ReadAllText(Path.Combine(root, "build", "package", "New-ManualAcceptanceBundle.ps1"));
        var finalizer = File.ReadAllText(Path.Combine(root, "tools", "PhysicalAcceptance", "Finalize-Windows10PhysicalAcceptance.ps1"));

        Assert.Contains("AcceptanceContracts.ps1", bundleGenerator, StringComparison.Ordinal);
        Assert.Contains("payloadFiles.Add('AcceptanceContracts.ps1')", bundleGenerator, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $acceptanceContracts -Destination (Join-Path $bundle 'AcceptanceContracts.ps1')", bundleGenerator, StringComparison.Ordinal);
        Assert.Contains("Write-NewJsonArtifact", bundleGenerator, StringComparison.Ordinal);
        Assert.Contains("FileMode]::CreateNew", bundleGenerator, StringComparison.Ordinal);
        Assert.DoesNotContain("$switch]$Force", bundleGenerator, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-Item -Force", bundleGenerator, StringComparison.Ordinal);
        Assert.Contains("Join-Path $PSScriptRoot 'AcceptanceContracts.ps1'", finalizer, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows10PhysicalPreflightIsReadOnlyFailClosedAndWritesOnlyNewEvidence()
    {
        var root = FindRoot();
        var verifier = File.ReadAllText(Path.Combine(root, "tools", "PhysicalAcceptance", "Verify-Windows10PhysicalAcceptance.ps1"));

        Assert.Contains("EvidenceRoot", verifier, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparsePath", verifier, StringComparison.Ordinal);
        Assert.Contains("Get-Partition -DriveLetter", verifier, StringComparison.Ordinal);
        Assert.Contains("Get-Disk -Number", verifier, StringComparison.Ordinal);
        Assert.Contains("ExistingProductAndHistoryAbsent", verifier, StringComparison.Ordinal);
        Assert.Contains("Get-ItemProperty -LiteralPath $uninstallKey.PSPath -ErrorAction Stop", verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-ItemProperty -Path $uninstallPaths -ErrorAction SilentlyContinue", verifier, StringComparison.Ordinal);
        Assert.Contains("AgentServiceNameAvailable", verifier, StringComparison.Ordinal);
        Assert.Contains("Get-SmbShare -ErrorAction Stop", verifier, StringComparison.Ordinal);
        Assert.Contains("SCAcc|SC[_-]?ACCEPTANCE", verifier, StringComparison.Ordinal);
        Assert.Contains("Win32_UserProfile", verifier, StringComparison.Ordinal);
        Assert.Contains("UnloadedUserProfileHivesAbsent", verifier, StringComparison.Ordinal);
        Assert.Contains("ProfilesNotSafelyInspectable", verifier, StringComparison.Ordinal);
        Assert.Contains("[IO.FileMode]::CreateNew", verifier, StringComparison.Ordinal);
        Assert.Contains("AcceptanceEligible = $false", verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Content", verifier, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-Item", verifier, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("New-Item", verifier, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrivilegedShareAndServiceCleanupRequiresRunOwnership()
    {
        var root = FindRoot();
        var privilegedTests = File.ReadAllText(Path.Combine(root, "tests", "StorageChronicle.Platform.Windows.Integration.Tests", "WindowsPrivilegedAcceptanceTests.cs"));
        var environment = File.ReadAllText(Path.Combine(root, "tests", "StorageChronicle.Platform.Windows.Integration.Tests", "WindowsAcceptanceEnvironment.cs"));

        Assert.Contains("shareCreatedByThisRun = false", privilegedTests, StringComparison.Ordinal);
        Assert.Contains("shareCreatedByThisRun = true", privilegedTests, StringComparison.Ordinal);
        Assert.Contains("GetSmbSharePathAsync(shareName)", privilegedTests, StringComparison.Ordinal);
        Assert.Contains("Path.GetFullPath(currentSharePath).Equals(Path.GetFullPath(scenario)", privilegedTests, StringComparison.Ordinal);
        Assert.Contains("if (shareCreatedByThisRun)", privilegedTests, StringComparison.Ordinal);
        Assert.Contains("serviceCreatedByThisRun = false", privilegedTests, StringComparison.Ordinal);
        Assert.Contains("serviceCreatedByThisRun = true", privilegedTests, StringComparison.Ordinal);
        Assert.Contains("GetServiceExecutablePathAsync(serviceName)", privilegedTests, StringComparison.Ordinal);
        Assert.Contains("Path.GetFullPath(currentExecutable).Equals(Path.GetFullPath(executable)", privilegedTests, StringComparison.Ordinal);
        Assert.Contains("if (serviceCreatedByThisRun)", privilegedTests, StringComparison.Ordinal);
        Assert.Contains("Get-CimInstance -ClassName Win32_Service", environment, StringComparison.Ordinal);
        Assert.Contains("Get-SmbShare -Name $shareName", environment, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "TOP_CODEX.md"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
