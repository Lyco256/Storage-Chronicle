namespace StorageChronicle.SessionAgent;

/// <summary>Session agent entry point; a real deployment hosts the hidden notification window in the logged-on session.</summary>
public static class Program
{
    /// <summary>Starts the session agent without polling clipboard state.</summary>
    public static Task Main(string[] args) => Task.CompletedTask;
}
