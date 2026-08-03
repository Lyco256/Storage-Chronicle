using System.Threading.Channels;
using StorageChronicle.Platform.Windows.FileSystem.Interop;

namespace StorageChronicle.Platform.Windows.FileSystem.Volumes;

/// <summary>Streams event-driven external media arrivals and removals.</summary>
public sealed class WindowsExternalMediaMonitor : IAsyncDisposable
{
    private readonly Channel<ExternalMediaChange> changes;
    private readonly IDisposable registration;
    private readonly string? registrationFailure;
    private int disposed;
    private int overflowed;

    /// <summary>Initializes the monitor and registers the Configuration Manager callback.</summary>
    public WindowsExternalMediaMonitor(IWindowsDeviceNotificationNative? native = null, int queueCapacity = 64)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        changes = Channel.CreateBounded<ExternalMediaChange>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        var deviceNative = native ?? new WindowsNativeApi();
        try
        {
            registration = deviceNative.Register(PublishChange);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            registration = NoopDisposable.Instance;
            registrationFailure = exception.Message;
        }
    }

    /// <summary>Gets the registration failure, if Windows notification registration was unavailable.</summary>
    public string? RegistrationFailure => registrationFailure;

    /// <summary>Reads connection changes until cancelled.</summary>
    public async IAsyncEnumerable<ExternalMediaChange> ReadChangesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await changes.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Interlocked.Exchange(ref overflowed, 0) != 0)
            {
                yield return new ExternalMediaChange(ExternalMediaChangeKind.ContinuityGap, DateTimeOffset.UtcNow, null, "The external-media notification queue overflowed; reconciliation is required.");
            }

            while (changes.Reader.TryRead(out var change)) yield return change;
        }

        if (Interlocked.Exchange(ref overflowed, 0) != 0)
        {
            yield return new ExternalMediaChange(ExternalMediaChangeKind.ContinuityGap, DateTimeOffset.UtcNow, null, "The external-media notification queue overflowed; reconciliation is required.");
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

    private void PublishChange(ExternalMediaChangeKind kind)
    {
        if (!changes.Writer.TryWrite(new ExternalMediaChange(kind, DateTimeOffset.UtcNow, null)))
        {
            Interlocked.Exchange(ref overflowed, 1);
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
