using System.Threading.Channels;
using StorageChronicle.Platform.Windows.FileSystem.Interop;

namespace StorageChronicle.Platform.Windows.FileSystem.Volumes;

/// <summary>Streams event-driven external media arrivals and removals.</summary>
public sealed class WindowsExternalMediaMonitor : IAsyncDisposable
{
    private readonly Channel<ExternalMediaChange> changes = Channel.CreateUnbounded<ExternalMediaChange>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly IDisposable registration;
    private int disposed;

    /// <summary>Initializes the monitor and registers the Configuration Manager callback.</summary>
    public WindowsExternalMediaMonitor(IWindowsDeviceNotificationNative? native = null)
    {
        var deviceNative = native ?? new WindowsNativeApi();
        registration = deviceNative.Register(kind => changes.Writer.TryWrite(new ExternalMediaChange(kind, DateTimeOffset.UtcNow, null)));
    }

    /// <summary>Reads connection changes until cancelled.</summary>
    public async IAsyncEnumerable<ExternalMediaChange> ReadChangesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var change in changes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return change;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            registration.Dispose();
            changes.Writer.TryComplete();
        }

        return ValueTask.CompletedTask;
    }
}
