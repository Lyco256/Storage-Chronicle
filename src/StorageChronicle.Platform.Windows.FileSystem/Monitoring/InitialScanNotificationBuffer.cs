namespace StorageChronicle.Platform.Windows.FileSystem.Monitoring;

/// <summary>Provides a bounded, ordered notification buffer used across an initial-scan boundary.</summary>
public sealed class InitialScanNotificationBuffer
{
    private readonly int capacity;
    private readonly Queue<DirectoryChangeNotification> queue = new();
    private readonly object gate = new();
    private bool closed;

    /// <summary>Initializes a bounded notification buffer.</summary>
    public InitialScanNotificationBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.capacity = capacity;
    }

    /// <summary>Gets the sequence at which the initial scan began.</summary>
    public long BoundarySequence { get; private set; }

    /// <summary>Gets whether a notification was lost because the buffer reached its bound.</summary>
    public bool IsOverflowed { get; private set; }

    /// <summary>Starts the bounded interval at a known monitor sequence.</summary>
    public void Begin(long boundarySequence)
    {
        lock (gate)
        {
            if (closed || queue.Count != 0) throw new InvalidOperationException("The initial-scan buffer has already started.");
            BoundarySequence = boundarySequence;
        }
    }

    /// <summary>Adds a notification, retaining ordering and reporting overflow instead of dropping silently.</summary>
    public bool TryAdd(DirectoryChangeNotification notification)
    {
        lock (gate)
        {
            if (closed || IsOverflowed) return false;
            if (queue.Count >= capacity)
            {
                IsOverflowed = true;
                return false;
            }

            queue.Enqueue(notification);
            return true;
        }
    }

    /// <summary>Closes the interval and returns notifications in source order.</summary>
    public IReadOnlyList<DirectoryChangeNotification> Complete()
    {
        lock (gate)
        {
            closed = true;
            return queue.OrderBy(static item => item.Sequence).ToArray();
        }
    }
}
