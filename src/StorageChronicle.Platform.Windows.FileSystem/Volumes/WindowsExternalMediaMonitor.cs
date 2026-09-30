using System.Threading.Channels;
using StorageChronicle.Platform.Windows.FileSystem.Interop;

namespace StorageChronicle.Platform.Windows.FileSystem.Volumes;

/// <summary>Streams event-driven external media arrivals and removals.</summary>
public sealed class WindowsExternalMediaMonitor : IAsyncDisposable
{
    private readonly Channel<ExternalMediaChange> changes;
    private readonly IDisposable registration;
    private readonly object gate = new();
    private readonly int queueCapacity;
    private int queuedChanges;
    private bool overflowPending;
    private int disposed;

    /// <summary>Initializes the monitor and registers the Configuration Manager callback.</summary>
    /// <param name="native">Optional native device-notification boundary.</param>
    /// <param name="queueCapacity">Maximum number of pending connection events before a rescan signal is queued.</param>
    public WindowsExternalMediaMonitor(IWindowsDeviceNotificationNative? native = null, int queueCapacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(queueCapacity, 65_536);
        this.queueCapacity = queueCapacity;
        changes = Channel.CreateBounded<ExternalMediaChange>(new BoundedChannelOptions(checked(queueCapacity + 1))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
        var deviceNative = native ?? new WindowsNativeApi();
        registration = deviceNative.Register(OnDeviceChange);
    }

    /// <summary>Reads connection changes and explicit rescan-required signals until cancelled or disposed.</summary>
    public async IAsyncEnumerable<ExternalMediaChange> ReadChangesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var change in changes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (gate)
            {
                if (change.Kind == ExternalMediaChangeKind.RescanRequired)
                {
                    overflowPending = false;
                }
                else if (queuedChanges > 0)
                {
                    queuedChanges--;
                }
            }

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

    private void OnDeviceChange(ExternalMediaChangeKind kind)
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0 || overflowPending) return;
            if (queuedChanges >= queueCapacity)
            {
                overflowPending = true;
                // The physical channel has one reserved slot beyond the normal-event bound.
                // Callback code never waits for a reader or channel capacity.
                _ = changes.Writer.TryWrite(new ExternalMediaChange(ExternalMediaChangeKind.RescanRequired, DateTimeOffset.UtcNow, null));
                return;
            }

            if (changes.Writer.TryWrite(new ExternalMediaChange(kind, DateTimeOffset.UtcNow, null))) queuedChanges++;
        }
    }
}
