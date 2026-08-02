using System.Collections.Immutable;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.Session;

/// <summary>Represents a clipboard file intent without retaining clipboard contents.</summary>
public sealed record ClipboardIntent(long Generation, IReadOnlyList<string> Paths, bool IsCut, DateTimeOffset ObservedUtc, string Stage);

/// <summary>Tracks clipboard generations and emits only event-driven notifications.</summary>
public sealed class ClipboardIntentTracker
{
    private readonly Dictionary<long, ClipboardIntent> candidates = new();
    /// <summary>Registers a CF_HDROP-equivalent notification.</summary>
    public ClipboardIntent Observe(long generation, IReadOnlyList<string> paths, bool isCut)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var intent = new ClipboardIntent(generation, paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(), isCut, DateTimeOffset.UtcNow, "ConfirmedIntent");
        candidates[generation] = intent;
        return intent;
    }
    /// <summary>Returns a candidate for a paste; the same generation may be pasted repeatedly.</summary>
    public ClipboardIntent? ConfirmPaste(long generation) => candidates.TryGetValue(generation, out var value) ? value with { Stage = "Paste" } : null;
    /// <summary>Discards a candidate after the clipboard generation changes.</summary>
    public void ClearExcept(long generation) { foreach (var key in candidates.Keys.Where(key => key != generation).ToArray()) candidates.Remove(key); }
}

/// <summary>Validates that a session-agent client can only send clipboard candidates for its own session.</summary>
public sealed class SessionIpcGuard
{
    /// <summary>Checks client SID and target session id.</summary>
    public bool IsAllowed(string clientSid, string expectedSid, int clientSessionId, int targetSessionId, string messageType) =>
        string.Equals(clientSid, expectedSid, StringComparison.OrdinalIgnoreCase) && clientSessionId == targetSessionId && messageType == "ClipboardCandidate";
}

/// <summary>Provides capability detection for local cloud placeholder metadata only.</summary>
public sealed class CloudPlaceholderCapability : IPlatformCapabilities
{
    /// <inheritdoc />
    public bool IsSupported(string capability) => capability is "CloudPlaceholder" && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
}

/// <summary>Represents a local share snapshot without remote user or read-access history.</summary>
public sealed record LocalShare(string Name, string LocalPath, string Type, string? Description, IReadOnlyList<string> Permissions);

/// <summary>Computes deterministic SMB share changes between snapshots.</summary>
public sealed class ShareSnapshotDiffer
{
    /// <summary>Returns dedicated ShareChanged source-like facts for added, removed, or changed shares.</summary>
    public IReadOnlyList<LocalShareChange> Diff(IReadOnlyList<LocalShare> before, IReadOnlyList<LocalShare> after)
    {
        var oldByName = before.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        var changes = new List<LocalShareChange>();
        foreach (var value in after)
        {
            if (!oldByName.TryGetValue(value.Name, out var old)) changes.Add(new LocalShareChange(value, "Created"));
            else if (!Equals(old, value)) changes.Add(new LocalShareChange(value, "Changed"));
            oldByName.Remove(value.Name);
        }
        changes.AddRange(oldByName.Values.Select(value => new LocalShareChange(value, "Deleted")));
        return changes;
    }
}

/// <summary>Describes a share state change; it is not a file metadata change.</summary>
public sealed record LocalShareChange(LocalShare Share, string ChangeKind);
