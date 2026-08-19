using Xunit;

namespace StorageChronicle.Installer.Tests;

public sealed class InstallerManifestTests
{
    [Fact]
    public void ManifestContainsSingleProductAndRecoveryContract()
    {
        var root = FindRoot();
        var wix = File.ReadAllText(Path.Combine(root, "installer", "StorageChronicle.wxs"));
        Assert.Contains("Storage Chronicle", wix, StringComparison.Ordinal);
        Assert.Contains("Account=\"LocalSystem\"", wix, StringComparison.Ordinal);
        Assert.Contains("ServiceConfig", wix, StringComparison.Ordinal);
        Assert.Contains("RestartServiceDelayInSeconds=\"5\"", wix, StringComparison.Ordinal);
        Assert.Contains("distinct 5/15/60-second delays", wix, StringComparison.Ordinal);
        var recovery = File.ReadAllText(Path.Combine(root, "src", "StorageChronicle.Agent", "WindowsServiceRecoveryConfigurator.cs"));
        Assert.Contains("5_000", recovery, StringComparison.Ordinal);
        Assert.Contains("15_000", recovery, StringComparison.Ordinal);
        Assert.Contains("60_000", recovery, StringComparison.Ordinal);
        Assert.Contains("ChangeServiceConfig2", recovery, StringComparison.Ordinal);
        Assert.Contains("CurrentVersion\\Run", wix, StringComparison.Ordinal);
        Assert.Contains("CommonAppDataFolder", wix, StringComparison.Ordinal);
        Assert.Contains("Permanent=\"yes\"", wix, StringComparison.Ordinal);
        Assert.DoesNotContain("Driver", wix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HyperVInstallerDriverIsExplicitAndFailClosed()
    {
        var root = FindRoot();
        var genericHarness = File.ReadAllText(Path.Combine(root, "build", "package", "Test-Installer.ps1"));
        var driver = File.ReadAllText(Path.Combine(root, "tools", "PhysicalAcceptance", "Invoke-HyperVInstallerCase.ps1"));
        var orchestrator = File.ReadAllText(Path.Combine(root, "tools", "TestEnvironment", "Run-HyperVInstallerAcceptance.ps1"));

        Assert.Contains("GuestCredentialReference", genericHarness, StringComparison.Ordinal);
        Assert.Contains("'PhysicalMachine', 'HyperVVm'", genericHarness, StringComparison.Ordinal);
        Assert.Contains("New-PSSession -VMName", driver, StringComparison.Ordinal);
        Assert.Contains("Copy-Item", driver, StringComparison.Ordinal);
        Assert.Contains("-ToSession $Session", driver, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -FromSession $Session", driver, StringComparison.Ordinal);
        Assert.Contains("Status = 'FAILED'", driver, StringComparison.Ordinal);
        Assert.Contains("SC-CLEAN-BASELINE", orchestrator, StringComparison.Ordinal);
        Assert.Contains("-Apply", orchestrator, StringComparison.Ordinal);
        Assert.Contains("AcceptanceEligible", orchestrator, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows10AcceptanceComposersRequireRealEvidence()
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
        Assert.Contains("SC-Test-W10", host, StringComparison.Ordinal);
        Assert.Contains("Assert-HyperVMutationPrerequisites", host, StringComparison.Ordinal);
        Assert.Contains("if (-not $Apply)", host, StringComparison.Ordinal);
        Assert.Contains("AcceptanceEligible = $false", host, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivilegedAcceptanceUsesOneFixedCapabilityContract()
    {
        var root = FindRoot();
        var contract = File.ReadAllText(Path.Combine(root, "build", "quality", "AcceptanceContracts.ps1"));
        var producer = File.ReadAllText(Path.Combine(root, "build", "Test-Privileged.ps1"));
        var stageA = File.ReadAllText(Path.Combine(root, "tools", "TestEnvironment", "Compose-Windows10StageA.ps1"));
        var finalGate = File.ReadAllText(Path.Combine(root, "build", "quality", "Test-FinalAcceptance.ps1"));

        foreach (var capability in new[] { "Vhdx", "UsnQuery", "UsnRead", "Mft", "Reconciliation", "Etw", "ReadDirectoryChangesW", "BufferGap", "Smb", "Service", "SessionAgent", "Clipboard", "VolumeGuid", "HotAttachDetach", "AclDeniedMetadata", "NonNtfs" })
        {
            Assert.Contains($"'{capability}'", contract, StringComparison.Ordinal);
        }

        Assert.Contains("AcceptanceContracts.ps1", producer, StringComparison.Ordinal);
        Assert.Contains("Get-RequiredWindowsPrivilegedCapabilities", producer, StringComparison.Ordinal);
        Assert.Contains("Get-RequiredWindowsPrivilegedCapabilities", stageA, StringComparison.Ordinal);
        Assert.Contains("Get-RequiredWindowsPrivilegedCapabilities", finalGate, StringComparison.Ordinal);
        Assert.Contains("Windows 11", finalGate, StringComparison.Ordinal);
        Assert.Contains("IsAdministrator", finalGate, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalAcceptanceRequiresPhysicalAndHyperVPrerequisites()
    {
        var root = FindRoot();
        var finalGate = File.ReadAllText(Path.Combine(root, "build", "quality", "Test-FinalAcceptance.ps1"));
        var resource = File.ReadAllText(Path.Combine(root, "build", "quality", "Test-ResourceBudgetAcceptance.ps1"));
        var mft = File.ReadAllText(Path.Combine(root, "build", "quality", "Test-FullBenchmarkMatrix.ps1"));
        var windows10Finalizer = File.ReadAllText(Path.Combine(root, "tools", "PhysicalAcceptance", "Finalize-Windows10PhysicalAcceptance.ps1"));
        var agent = File.ReadAllText(Path.Combine(root, "src", "StorageChronicle.Agent", "Program.cs"));
        var testLab = File.ReadAllText(Path.Combine(root, "tools", "TestEnvironment", "Invoke-WindowsTestLab.ps1"));

        Assert.Contains("Windows11HyperVInstallerManifest", finalGate, StringComparison.Ordinal);
        Assert.Contains("Assert-Windows11HyperVInstallerPrerequisite", finalGate, StringComparison.Ordinal);
        Assert.Contains("AgentIntegration-Windows11", finalGate, StringComparison.Ordinal);
        Assert.Contains("IsPhysicalMachine", finalGate, StringComparison.Ordinal);
        Assert.Contains("Configuration -ne 'Release'", finalGate, StringComparison.Ordinal);
        Assert.Contains("SC_TEST_MFT_VOLUME", finalGate, StringComparison.Ordinal);
        Assert.Contains("Windows 11 x64 physical release machine", resource, StringComparison.Ordinal);
        Assert.Contains("STORAGE_CHRONICLE_MFT_VOLUME_LABEL", mft, StringComparison.Ordinal);
        Assert.Contains("Get-RequiredInstallerCaseIds", windows10Finalizer, StringComparison.Ordinal);
        Assert.Contains("Status -ne 'PASSED'", windows10Finalizer, StringComparison.Ordinal);
        Assert.Contains("--testlab", agent, StringComparison.Ordinal);
        Assert.Contains("--testlab", testLab, StringComparison.Ordinal);
        Assert.Contains("ExecutionMode = 'TestLab'", testLab, StringComparison.Ordinal);
        Assert.Contains("Diagnostic = $false", testLab, StringComparison.Ordinal);
        Assert.Contains("not an eligible non-diagnostic TestLab execution", finalGate, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows10ManualBundleCarriesSharedAcceptanceContract()
    {
        var root = FindRoot();
        var bundleGenerator = File.ReadAllText(Path.Combine(root, "build", "package", "New-ManualAcceptanceBundle.ps1"));
        var finalizer = File.ReadAllText(Path.Combine(root, "tools", "PhysicalAcceptance", "Finalize-Windows10PhysicalAcceptance.ps1"));

        Assert.Contains("AcceptanceContracts.ps1", bundleGenerator, StringComparison.Ordinal);
        Assert.Contains("payloadFiles.Add('AcceptanceContracts.ps1')", bundleGenerator, StringComparison.Ordinal);
        Assert.Contains("Join-Path $PSScriptRoot 'AcceptanceContracts.ps1'", finalizer, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "TOP_CODEX.md"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
