using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.FileSystem.Interop;

namespace StorageChronicle.Platform.Windows.FileSystem.Monitoring;

/// <summary>Runs event-driven ReadDirectoryChangesW monitoring without periodic full-volume scans.</summary>
public sealed class WindowsDirectoryChangeMonitor
{
    private const int ErrorNotifyEnumDir = 1022;
    private const int ErrorInvalidHandle = 6;
    private const int ErrorDeviceNotConnected = 1167;
    private const int ErrorPathNotFound = 3;
    private readonly VolumeId volumeId;
    private readonly string directoryPath;
    private readonly IWindowsFileMetadataNative fileNative;
    private readonly IWindowsDirectoryChangeNative changeNative;
    private readonly int bufferSize;

    /// <summary>Initializes a ReadDirectoryChangesW monitor.</summary>
    public WindowsDirectoryChangeMonitor(VolumeId volumeId, string directoryPath, IWindowsFileMetadataNative fileNative, IWindowsDirectoryChangeNative changeNative, int bufferSize = 64 * 1024)
    {
        this.volumeId = volumeId;
        this.directoryPath = directoryPath;
        this.fileNative = fileNative;
        this.changeNative = changeNative;
        this.bufferSize = bufferSize;
    }

    /// <summary>Reads notifications until cancellation, handle loss, or a continuity gap.</summary>
    public async IAsyncEnumerable<DirectoryChangeRead> ReadChangesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = System.Threading.Channels.Channel.CreateBounded<DirectoryChangeRead>(new System.Threading.Channels.BoundedChannelOptions(1)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        var producer = ProduceChangesAsync(channel.Writer, producerCancellation.Token);
        try
        {
            await foreach (var read in channel.Reader.ReadAllAsync(producerCancellation.Token).ConfigureAwait(false))
            {
                yield return read;
            }
        }
        finally
        {
            producerCancellation.Cancel();
            try { await producer.ConfigureAwait(false); }
            catch (OperationCanceledException) when (producerCancellation.IsCancellationRequested) { }
        }
    }

    private async Task ProduceChangesAsync(System.Threading.Channels.ChannelWriter<DirectoryChangeRead> writer, CancellationToken cancellationToken)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = fileNative.OpenDirectory(directoryPath);
            var sequence = 0L;
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await changeNative.ReadChangesAsync(handle, bufferSize, cancellationToken).ConfigureAwait(false);
                if (read.ErrorCode != 0 || read.BytesReturned == 0)
                {
                    await writer.WriteAsync(new DirectoryChangeRead(Array.Empty<DirectoryChangeNotification>(), CreateGap(read.ErrorCode), true, read.ErrorCode), cancellationToken).ConfigureAwait(false);
                    break;
                }

                var parsed = ReadDirectoryChangesBufferParser.Parse(read.Buffer, read.BytesReturned, sequence, DateTimeOffset.UtcNow);
                if (parsed.IsMalformed)
                {
                    await writer.WriteAsync(new DirectoryChangeRead(Array.Empty<DirectoryChangeNotification>(), CreateGap(ErrorNotifyEnumDir, "Malformed ReadDirectoryChangesW buffer"), true, ErrorNotifyEnumDir), cancellationToken).ConfigureAwait(false);
                    break;
                }

                sequence += parsed.Notifications.Count;
                await writer.WriteAsync(new DirectoryChangeRead(parsed.Notifications, null, false, 0), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (UnauthorizedAccessException)
        {
            await TryWriteTerminalGapAsync(writer, CreateGap(5, "Access denied while opening or monitoring directory"), 5, cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception exception)
        {
            await TryWriteTerminalGapAsync(writer, CreateGap(exception.NativeErrorCode, "Windows API failed while opening or monitoring directory"), exception.NativeErrorCode, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException)
        {
            await TryWriteTerminalGapAsync(writer, CreateGap(ErrorPathNotFound, "Directory disappeared during monitoring"), ErrorPathNotFound, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            var error = exception.HResult & 0xFFFF;
            await TryWriteTerminalGapAsync(writer, CreateGap(error, "I/O failure while monitoring directory"), error, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            handle?.Dispose();
            writer.TryComplete();
        }
    }

    private static async ValueTask TryWriteTerminalGapAsync(System.Threading.Channels.ChannelWriter<DirectoryChangeRead> writer, ContinuityGap gap, int errorCode, CancellationToken cancellationToken)
    {
        try
        {
            await writer.WriteAsync(new DirectoryChangeRead(Array.Empty<DirectoryChangeNotification>(), gap, true, errorCode), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation ends this monitor; no reader remains that could consume a terminal gap.
        }
    }

    private ContinuityGap CreateGap(int nativeErrorCode, string? overrideReason = null)
    {
        var reason = overrideReason ?? nativeErrorCode switch
        {
            ErrorNotifyEnumDir => "ReadDirectoryChangesW notification buffer overflow or directory reconciliation required",
            ErrorInvalidHandle => "ReadDirectoryChangesW monitoring handle was lost",
            ErrorDeviceNotConnected => "Media was forcibly removed while monitoring",
            ErrorPathNotFound => "Monitored directory disappeared",
            _ => new Win32Exception(nativeErrorCode).Message
        };
        var now = DateTimeOffset.UtcNow;
        return new ContinuityGap(volumeId, now, now, reason);
    }
}
