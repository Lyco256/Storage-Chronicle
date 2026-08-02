using System.IO.Pipes;
using StorageChronicle.Platform.Windows.Session;

namespace StorageChronicle.SessionAgent;

/// <summary>Session agent entry point for the logged-on user session.</summary>
public static class Program
{
    /// <summary>Starts the session lifetime and waits for logoff or process shutdown.</summary>
    public static async Task Main(string[] args)
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        try
        {
            if (args.Contains("--pipe", StringComparer.OrdinalIgnoreCase))
            {
                await RunPipeLoopAsync(args, shutdown.Token).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Normal session shutdown.
        }
    }

    private static async Task RunPipeLoopAsync(string[] args, CancellationToken cancellationToken)
    {
        var pipeName = GetPipeName(args);
        await using var notifications = new WindowsClipboardNotificationSource();
        await using var clipboardSource = new ClipboardEventSource(notifications, new WindowsClipboardReader());
        var server = new SessionAgentPipeServer(clipboardSource);
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            _ = await server.RunAsync(pipe, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GetPipeName(IReadOnlyList<string> args)
    {
        var index = Array.FindIndex(args.ToArray(), value => string.Equals(value, "--pipe-name", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Count && !string.IsNullOrWhiteSpace(args[index + 1]))
        {
            return args[index + 1];
        }

        return "StorageChronicle.SessionAgent";
    }
}
