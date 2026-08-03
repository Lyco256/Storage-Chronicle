using System.IO.Pipes;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Platform.Windows.Session;

namespace StorageChronicle.SessionAgent;

/// <summary>Session process that observes clipboard metadata and forwards only bounded candidates to the Agent.</summary>
public static class Program
{
    /// <summary>Starts the session lifetime and waits for logoff or process shutdown.</summary>
    public static async Task Main(string[] args)
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
        try { await RunClientLoopAsync(GetPipeName(args), shutdown.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    private static async Task RunClientLoopAsync(string pipeName, CancellationToken cancellationToken)
    {
        await using var notifications = new WindowsClipboardNotificationSource();
        await using var clipboardSource = new ClipboardEventSource(notifications, new WindowsClipboardReader());
        await foreach (var source in clipboardSource.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            await SendAsync(pipeName, ClipboardCandidateMessage.FromSourceEvent(source).ToAgentRequest(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendAsync(string pipeName, ClipboardCandidateRequest request, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(250);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SendOnceAsync(pipeName, request, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or EndOfStreamException or InvalidDataException)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task SendOnceAsync(string pipeName, ClipboardCandidateRequest request, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        var codec = new LengthPrefixedJsonCodec();
        var hello = codec.Encode(IpcProtocol.Create("ClientHello", new IpcClientHello(IpcClientRole.SessionAgent, System.Diagnostics.Process.GetCurrentProcess().SessionId)), IpcProtocol.Major, IpcProtocol.Minor);
        await pipe.WriteAsync(hello, cancellationToken).ConfigureAwait(false);
        var frame = codec.Encode(IpcProtocol.Create("ClipboardCandidate", request), IpcProtocol.Major, IpcProtocol.Minor);
        await pipe.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(pipe, header, cancellationToken).ConfigureAwait(false);
        var length = BitConverter.ToInt32(header, 0);
        if (length is < 0 or > LengthPrefixedJsonCodec.MaximumPayloadBytes) throw new InvalidDataException("Agent response length is invalid.");
        var payload = new byte[length];
        await ReadExactlyAsync(pipe, payload, cancellationToken).ConfigureAwait(false);
        var responseFrame = new byte[sizeof(int) + length];
        header.CopyTo(responseFrame, 0);
        payload.CopyTo(responseFrame, sizeof(int));
        _ = codec.Decode<IpcEnvelope>(responseFrame, IpcProtocol.Major);
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Agent response ended early.");
            offset += read;
        }
    }

    private static string GetPipeName(IReadOnlyList<string> args)
    {
        var index = Array.FindIndex(args.ToArray(), value => string.Equals(value, "--pipe-name", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Count && !string.IsNullOrWhiteSpace(args[index + 1]) ? args[index + 1] : "StorageChronicle.Agent";
    }
}
