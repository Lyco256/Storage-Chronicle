using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.Session;

/// <summary>Reads local Windows file attributes for cloud placeholder state only.</summary>
public interface ICloudPlaceholderReader
{
    /// <summary>Reads local hydration attributes without calling a cloud API.</summary>
    ValueTask<CloudPlaceholderObservation> ReadStateAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Maps Windows placeholder attributes to bounded local source information.</summary>
public sealed class WindowsCloudPlaceholderReader : ICloudPlaceholderReader
{
    private const FileAttributes PlaceholderUnpinned = (FileAttributes)0x00100000;
    private const FileAttributes RecallOnData = (FileAttributes)0x00400000;
    private readonly ISessionClock clock;
    private readonly CloudPlaceholderCapability capability;

    /// <summary>Creates a local-only placeholder reader.</summary>
    public WindowsCloudPlaceholderReader(ISessionClock? clock = null, CloudPlaceholderCapability? capability = null)
    {
        this.clock = clock ?? new SystemSessionClock();
        this.capability = capability ?? new CloudPlaceholderCapability();
    }

    /// <inheritdoc />
    public ValueTask<CloudPlaceholderObservation> ReadStateAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.UtcNow;
        if (!capability.IsSupported("CloudPlaceholderMetadata"))
        {
            return ValueTask.FromResult(new CloudPlaceholderObservation(path, CloudPlaceholderState.Unsupported, false, now, EventQuality.Unknown));
        }

        try
        {
            var attributes = File.GetAttributes(path);
            var state = (attributes & (FileAttributes.Offline | PlaceholderUnpinned | RecallOnData)) != 0
                ? CloudPlaceholderState.Dehydrated
                : CloudPlaceholderState.Hydrated;
            return ValueTask.FromResult(new CloudPlaceholderObservation(path, state, true, now, EventQuality.Exact));
        }
        catch (FileNotFoundException)
        {
            return ValueTask.FromResult(new CloudPlaceholderObservation(path, CloudPlaceholderState.SourceUnknown, true, now, EventQuality.Unknown));
        }
        catch (DirectoryNotFoundException)
        {
            return ValueTask.FromResult(new CloudPlaceholderObservation(path, CloudPlaceholderState.SourceUnknown, true, now, EventQuality.Unknown));
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult(new CloudPlaceholderObservation(path, CloudPlaceholderState.SourceUnknown, true, now, EventQuality.Unknown));
        }
        catch (IOException)
        {
            return ValueTask.FromResult(new CloudPlaceholderObservation(path, CloudPlaceholderState.SourceUnknown, true, now, EventQuality.Unknown));
        }
    }
}
