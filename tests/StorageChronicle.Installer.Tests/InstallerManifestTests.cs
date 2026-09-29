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

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "TOP_CODEX.md"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
