using StorageChronicle.Domain.Contracts;
using Xunit;

namespace StorageChronicle.Domain.Tests;

public sealed class EventModelTests
{
    [Fact]
    public void StrongIdsRejectBlankValues()
    {
        Assert.Throws<ArgumentException>(() => VolumeId.Create(" "));
        Assert.Throws<ArgumentException>(() => FileId.Create(""));
    }

    [Fact]
    public void SchemaHasStableCurrentVersion() => Assert.Equal(new EventSchemaVersion(1, 0), EventSchemaVersion.Current);
}
