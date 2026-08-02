using StorageChronicle.Platform.Windows.Session;
using Xunit;

namespace StorageChronicle.WindowsIntegration.Tests;

public sealed class WindowsCapabilityTests
{
    [Fact]
    public void CloudPlaceholderCapabilityIsExplicitlyWindows10Bound()
    {
        var capability = new CloudPlaceholderCapability();
        Assert.False(capability.IsSupported("UnknownCapability"));
        Assert.Equal(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041), capability.IsSupported("CloudPlaceholder"));
    }
}
