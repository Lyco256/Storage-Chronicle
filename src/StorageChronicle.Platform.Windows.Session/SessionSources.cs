using System.Collections.Immutable;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Abstractions;

namespace StorageChronicle.Platform.Windows.Session;

/// <summary>Supplies deterministic time to session sources and tests.</summary>
public interface ISessionClock
{
    /// <summary>Returns the current UTC time.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>Uses the system UTC clock for production session sources.</summary>
public sealed class SystemSessionClock : ISessionClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Quality assigned to a clipboard candidate before application correlation.</summary>
public enum ClipboardCandidateQuality
{
    /// <summary>Clipboard data included a readable CF_HDROP and a copy/cut intent.</summary>
    ConfirmedIntent,
    /// <summary>The candidate was correlated from a bounded session observation.</summary>
    CorrelatedSource,
    /// <summary>The clipboard was locked or could not be read completely.</summary>
    SourceUnknown,
    /// <summary>No file-drop candidate or intent was identified.</summary>
    NotIdentified
}

/// <summary>Represents a bounded clipboard file-intent candidate.</summary>
public sealed record ClipboardIntent(long Generation, IReadOnlyList<string> Paths, bool IsCut, DateTimeOffset ObservedUtc, string Stage)
{
    /// <summary>Gets the candidate quality without exposing clipboard contents.</summary>
    public ClipboardCandidateQuality Quality { get; init; } = ClipboardCandidateQuality.ConfirmedIntent;
}

/// <summary>Tracks clipboard generations and emits only event-driven notifications.</summary>
public sealed class ClipboardIntentTracker
{
    private readonly Dictionary<long, ClipboardIntent> candidates = new();
    private readonly ISessionClock clock;

    /// <summary>Creates a tracker using the system clock or an injected test clock.</summary>
    public ClipboardIntentTracker(ISessionClock? clock = null) => this.clock = clock ?? new SystemSessionClock();

    /// <summary>Registers a CF_HDROP-equivalent notification without retaining clipboard bytes.</summary>
    public ClipboardIntent Observe(long generation, IReadOnlyList<string> paths, bool isCut)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var normalized = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim()).ToImmutableArray();
        var intent = new ClipboardIntent(generation, normalized, isCut, clock.UtcNow, "ConfirmedIntent");
        candidates[generation] = intent;
        return intent;
    }

    /// <summary>Returns a candidate for a paste; the same generation may be pasted repeatedly.</summary>
    public ClipboardIntent? ConfirmPaste(long generation) => candidates.TryGetValue(generation, out var value) ? value with { Stage = "Paste", Quality = ClipboardCandidateQuality.CorrelatedSource } : null;

    /// <summary>Discards candidates from older clipboard generations.</summary>
    public void ClearExcept(long generation)
    {
        foreach (var key in candidates.Keys.Where(key => key != generation).ToArray())
        {
            candidates.Remove(key);
        }
    }
}

/// <summary>One event-driven clipboard update received by the hidden listener window.</summary>
public sealed record ClipboardNotification(DateTimeOffset ObservedUtc);

/// <summary>Clipboard values read from one notification without retaining clipboard content.</summary>
public sealed record ClipboardSnapshot(long Generation, IReadOnlyList<string> Paths, bool IsCut);

/// <summary>Describes the result of one non-blocking clipboard read attempt.</summary>
public enum ClipboardReadStatus
{
    /// <summary>A file-drop list and generation were read.</summary>
    Read,
    /// <summary>The clipboard was temporarily owned by another process.</summary>
    Locked,
    /// <summary>No CF_HDROP file list was present.</summary>
    Empty,
    /// <summary>The capability is not available on this platform.</summary>
    Unsupported
}

/// <summary>Result returned by an injected or native clipboard reader.</summary>
public sealed record ClipboardReadResult(ClipboardReadStatus Status, ClipboardSnapshot? Snapshot);

/// <summary>Supplies one clipboard read attempt after a format-change notification.</summary>
public interface IClipboardReader
{
    /// <summary>Reads only file-drop paths, copy/cut intent, and generation metadata.</summary>
    ValueTask<ClipboardReadResult> TryReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Receives clipboard changes without polling.</summary>
public interface IClipboardNotificationSource : IAsyncDisposable
{
    /// <summary>Streams WM_CLIPBOARDUPDATE notifications.</summary>
    IAsyncEnumerable<ClipboardNotification> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Controls bounded retry when another process temporarily owns the clipboard.</summary>
public sealed record ClipboardLockRetryOptions(int MaxAttempts, TimeSpan Delay)
{
    /// <summary>Default short non-blocking retry policy.</summary>
    public static ClipboardLockRetryOptions Default { get; } = new(4, TimeSpan.FromMilliseconds(10));

    /// <summary>Validates a retry policy.</summary>
    public ClipboardLockRetryOptions Validate()
    {
        if (MaxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        if (Delay < TimeSpan.Zero || Delay > TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(Delay));
        return this;
    }
}

/// <summary>Validates that a session-agent client can only send clipboard candidates for its own session.</summary>
public sealed class SessionIpcGuard
{
    /// <summary>Checks client SID, target session ID, and message type.</summary>
    public bool IsAllowed(string clientSid, string expectedSid, int clientSessionId, int targetSessionId, string messageType) =>
        string.Equals(clientSid, expectedSid, StringComparison.OrdinalIgnoreCase) && clientSessionId == targetSessionId && messageType == "ClipboardCandidate";
}

/// <summary>Provides capability detection for local cloud placeholder metadata only.</summary>
public sealed class CloudPlaceholderCapability : IPlatformCapabilities
{
    /// <inheritdoc />
    public bool IsSupported(string capability) => capability switch
    {
        "CloudPlaceholder" or "CloudPlaceholderMetadata" => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041),
        _ => false
    };
}

/// <summary>Classifies local placeholder state without calling a cloud service.</summary>
public enum CloudPlaceholderState
{
    /// <summary>Local capability is unavailable or state could not be read.</summary>
    SourceUnknown,
    /// <summary>Local file is hydrated and available.</summary>
    Hydrated,
    /// <summary>Local placeholder is dehydrated or recall-on-data.</summary>
    Dehydrated,
    /// <summary>Only local placeholder metadata changed.</summary>
    PlaceholderMetadataChanged,
    /// <summary>Local capability was explicitly disabled.</summary>
    Unsupported
}

/// <summary>Describes a local-only cloud placeholder observation.</summary>
public sealed record CloudPlaceholderObservation(string Path, CloudPlaceholderState State, bool CapabilityAvailable, DateTimeOffset ObservedUtc, EventQuality Quality);

/// <summary>Classifies a local placeholder transition for source metadata.</summary>
public static class CloudPlaceholderStateClassifier
{
    /// <summary>Returns a local transition without contacting a cloud API.</summary>
    public static CloudPlaceholderState Classify(CloudPlaceholderState? previous, CloudPlaceholderState current, bool metadataChanged) =>
        metadataChanged && previous == current ? CloudPlaceholderState.PlaceholderMetadataChanged : current;
}

/// <summary>Computes deterministic SMB share changes between shared-contract snapshots.</summary>
public sealed class ShareSnapshotDiffer
{
    /// <summary>Returns dedicated ShareChanged facts for added, removed, or changed shares.</summary>
    public IReadOnlyList<ShareChange> Diff(IReadOnlyList<ShareDescriptor> before, IReadOnlyList<ShareDescriptor> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var oldByName = before.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        var changes = new List<ShareChange>();
        foreach (var value in after)
        {
            if (!oldByName.TryGetValue(value.Name, out var old)) changes.Add(new ShareChange(value, "Created"));
            else if (!ShareEqual(old, value)) changes.Add(new ShareChange(value, "Changed"));
            oldByName.Remove(value.Name);
        }

        changes.AddRange(oldByName.Values.Select(value => new ShareChange(value, "Deleted")));
        return changes;
    }

    private static bool ShareEqual(ShareDescriptor left, ShareDescriptor right) =>
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.LocalPath, right.LocalPath, StringComparison.Ordinal) &&
        string.Equals(left.Type, right.Type, StringComparison.Ordinal) &&
        string.Equals(left.Description, right.Description, StringComparison.Ordinal) &&
        left.Permissions.SequenceEqual(right.Permissions, StringComparer.Ordinal);
}

/// <summary>Describes an SMB share state change separately from file metadata.</summary>
public sealed record ShareChange(ShareDescriptor Share, string ChangeKind);
