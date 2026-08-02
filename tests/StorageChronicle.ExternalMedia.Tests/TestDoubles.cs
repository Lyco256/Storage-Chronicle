using StorageChronicle.ExternalMedia;

namespace StorageChronicle.ExternalMedia.Tests;

internal sealed class ManualMediaClock : IMediaClock
{
    public ManualMediaClock(DateTimeOffset? initial = null) => UtcNow = initial ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public DateTimeOffset UtcNow { get; private set; }
    public void Advance(TimeSpan amount) => UtcNow = UtcNow.Add(amount);
}
