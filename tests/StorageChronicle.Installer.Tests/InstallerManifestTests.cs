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

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "TOP_CODEX.md"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
