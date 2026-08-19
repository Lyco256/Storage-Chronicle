using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Agent;

/// <summary>Describes the live events observed while one reconciliation session was active.</summary>
public sealed record ReconciliationLiveEventBatch(IReadOnlyList<SourceEvent> Events, bool Overflowed)
{
    /// <summary>Gets the number of captured live source facts.</summary>
    public int Count => Events.Count;
}

/// <summary>Provides a bounded, transient bridge between live durable commits and reconciliation.</summary>
public sealed class ReconciliationLiveEventBuffer
{
    private readonly object gate = new();
    private readonly Dictionary<VolumeId, ReconciliationLiveEventSession> sessions = new();
    private readonly int capacity;

    /// <summary>Initializes a bounded live-event buffer.</summary>
    public ReconciliationLiveEventBuffer(int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.capacity = capacity;
    }

    /// <summary>Starts a session at the last source sequence known to be continuous.</summary>
    public ReconciliationLiveEventSession Begin(VolumeId volumeId, long sourceSequenceBoundary)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceSequenceBoundary);
        lock (gate)
        {
            if (sessions.ContainsKey(volumeId)) throw new InvalidOperationException($"A reconciliation session is already active for volume {volumeId.Value}.");
            var session = new ReconciliationLiveEventSession(this, volumeId, sourceSequenceBoundary, capacity);
            sessions.Add(volumeId, session);
            return session;
        }
    }

    /// <summary>Records a source fact after its durable state commit, without blocking the live pipeline.</summary>
    public void Observe(SourceEvent source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.VolumeId is not { } volumeId) return;
        lock (gate)
        {
            if (!sessions.TryGetValue(volumeId, out var session)) return;
            session.TryCapture(source);
        }
    }

    private ReconciliationLiveEventBatch Complete(ReconciliationLiveEventSession session)
    {
        lock (gate)
        {
            if (!session.TryClose(out var batch)) return batch;
            sessions.Remove(session.VolumeId);
            return batch;
        }
    }

    /// <summary>Owns one volume-scoped capture window.</summary>
    public sealed class ReconciliationLiveEventSession : IDisposable
    {
        private readonly ReconciliationLiveEventBuffer owner;
        private readonly long sourceSequenceBoundary;
        private readonly int capacity;
        private readonly List<SourceEvent> events = [];
        private ReconciliationLiveEventBatch? completed;
        private bool overflowed;
        private bool closed;

        internal ReconciliationLiveEventSession(ReconciliationLiveEventBuffer owner, VolumeId volumeId, long sourceSequenceBoundary, int capacity)
        {
            this.owner = owner;
            VolumeId = volumeId;
            this.sourceSequenceBoundary = sourceSequenceBoundary;
            this.capacity = capacity;
        }

        /// <summary>Gets the volume covered by this session.</summary>
        public VolumeId VolumeId { get; }

        /// <summary>Gets the source sequence used as the session start boundary.</summary>
        public long SourceSequenceBoundary => sourceSequenceBoundary;

        internal void TryCapture(SourceEvent source)
        {
            if (closed || source.Time.SourceSequence.Value <= sourceSequenceBoundary) return;
            // Reconciliation facts are already produced by the runner and must not
            // be replayed as live facts.
            if (source.Properties.ContainsKey("reconciliationRunId")) return;
            if (events.Any(value => value.EventId == source.EventId)) return;
            if (events.Count >= capacity)
            {
                overflowed = true;
                return;
            }

            events.Add(source);
        }

        internal bool TryClose(out ReconciliationLiveEventBatch batch)
        {
            if (completed is not null)
            {
                batch = completed;
                return false;
            }

            closed = true;
            var ordered = events
                .OrderBy(value => value.Time.SourceSequence.Value)
                .ThenBy(value => value.Time.RecordedUtc)
                .ThenBy(value => value.EventId.ToString(), StringComparer.Ordinal)
                .ToArray();
            completed = new ReconciliationLiveEventBatch(ordered, overflowed);
            batch = completed;
            return true;
        }

        /// <inheritdoc />
        public void Dispose() => owner.Complete(this);

        /// <summary>Closes the session and returns its bounded capture.</summary>
        public ReconciliationLiveEventBatch Complete() => owner.Complete(this);
    }
}
