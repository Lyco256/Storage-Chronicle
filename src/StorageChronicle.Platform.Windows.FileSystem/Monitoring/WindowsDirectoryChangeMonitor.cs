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
    private readonly int readQueueCapacity;
    private readonly string? volumeRootPath;
    private readonly string[] rootComponents;

    /// <summary>Initializes a ReadDirectoryChangesW monitor.</summary>
    public WindowsDirectoryChangeMonitor(VolumeId volumeId, string directoryPath, IWindowsFileMetadataNative fileNative, IWindowsDirectoryChangeNative changeNative, int bufferSize = 64 * 1024, int readQueueCapacity = 16, string? volumeRootPath = null, IReadOnlyList<string>? rootComponents = null)
    {
        this.volumeId = volumeId;
        this.directoryPath = string.IsNullOrWhiteSpace(directoryPath) ? throw new ArgumentException("A monitor root path is required.", nameof(directoryPath)) : directoryPath;
        this.fileNative = fileNative ?? throw new ArgumentNullException(nameof(fileNative));
        this.changeNative = changeNative ?? throw new ArgumentNullException(nameof(changeNative));
        this.bufferSize = bufferSize > 0 ? bufferSize : throw new ArgumentOutOfRangeException(nameof(bufferSize));
        this.readQueueCapacity = readQueueCapacity > 0 ? readQueueCapacity : throw new ArgumentOutOfRangeException(nameof(readQueueCapacity));
        this.volumeRootPath = volumeRootPath is null ? null : string.IsNullOrWhiteSpace(volumeRootPath) ? throw new ArgumentException("A volume root path cannot be empty.", nameof(volumeRootPath)) : volumeRootPath;
        this.rootComponents = rootComponents?.ToArray() ?? Array.Empty<string>();
        if (this.volumeRootPath is null && this.rootComponents.Length > 0) throw new ArgumentException("Relative root components require a volume root path.", nameof(rootComponents));
    }

    /// <summary>Reads notifications until cancellation, handle loss, or a continuity gap.</summary>
    public async IAsyncEnumerable<DirectoryChangeRead> ReadChangesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<DirectoryChangeRead>(new System.Threading.Channels.BoundedChannelOptions(readQueueCapacity)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceChangesAsync(channel.Writer, producerCancellation.Token);
        try
        {
            await foreach (var read in channel.Reader.ReadAllAsync(producerCancellation.Token).ConfigureAwait(false))
            {
                yield return read;
            }

            await producer.ConfigureAwait(false);
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
            handle = OpenMonitorDirectory();
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

    private SafeFileHandle OpenMonitorDirectory()
    {
        if (volumeRootPath is null) return fileNative.OpenDirectory(directoryPath);

        var handle = fileNative.OpenDirectory(volumeRootPath);
        var currentPath = volumeRootPath;
        try
        {
            foreach (var component in rootComponents)
            {
                var nextHandle = fileNative.OpenChild(handle, component, FileAttributes.Directory);
                try
                {
                    var childPath = Path.Combine(currentPath, component);
                    var metadata = fileNative.ReadMetadataRelative(nextHandle, childPath, handle);
                    if (metadata.Kind != FileKind.Directory || (metadata.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException($"Configured monitoring root contains a reparse point or non-directory component: {component}");
                    }
                }
                catch
                {
                    nextHandle.Dispose();
                    throw;
                }

                handle.Dispose();
                handle = nextHandle;
                currentPath = Path.Combine(currentPath, component);
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
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
