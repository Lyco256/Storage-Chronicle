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
        _ = new ClipboardIntentTracker();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Normal session shutdown.
        }
    }
}
