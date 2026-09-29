using System.Collections.Immutable;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Normalization;

/// <summary>Configures bounded, transient normalization correlations.</summary>
public sealed record NormalizationOptions
{
    /// <summary>Gets the maximum number of remembered source events.</summary>
    public int MaximumRememberedEvents { get; init; } = 4096;

    /// <summary>Gets the maximum number of clipboard candidates retained.</summary>
    public int MaximumClipboardCandidates { get; init; } = 256;

    /// <summary>Gets the validity interval for a clipboard candidate.</summary>
    public TimeSpan ClipboardValidity { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Validates the option values.</summary>
    public void Validate()
    {
        if (MaximumRememberedEvents < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRememberedEvents), "At least one source event must be remembered.");
        }

        if (MaximumClipboardCandidates < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumClipboardCandidates), "At least one clipboard candidate must be retained.");
        }

        if (ClipboardValidity <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ClipboardValidity), "Clipboard validity must be positive.");
        }
    }
}

/// <summary>Reports a contradictory or malformed source event without discarding history silently.</summary>
public sealed class NormalizationException : Exception
{
    /// <summary>Initializes a normalization exception.</summary>
    public NormalizationException(string message) : base(message) { }
}

/// <summary>
/// Converts bounded source facts into immutable canonical events.
/// The instance owns only transient correlation state; it never reads or stores file contents.
/// </summary>
public sealed class EventNormalizer : IEventNormalizer
{
    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly ImmutableHashSet<string> SafePropertyNames =
        ImmutableHashSet.Create(KeyComparer,
            "operation", "observation", "oldParentFileId", "oldVolumeId", "oldFileId",
            "recycleAction", "isRecycleBin", "cloudState", "securityChanged",
            "clipboardGeneration", "clipboardIntent", "clipboardEffect", "sourcePathAvailable",
            "destinationPathAvailable", "copySourceFileId", "copyCorrelationQuality",
            "cutCorrelationQuality", "cutCorrelationRejected", "copyScope", "explorerOperation",
            "correlationQuality", "reconciliationReason", "reconciliationRunId", "reconciliationStatus", "reconciliationRequestId", "reconciliationDecision", "userDeclined", "uncertainFromUtc", "uncertainToUtc", "metadataQuality", "scanSequence", "mountReason", "volumeChange",
            "sourceRoute", "destinationRoute", "isRootChange", "path", "oldPath", "process.name", "process.executable", "process.parentInstanceId",
            "fileSystem",
            "media.logicalMediaId", "media.quality", "media.branch", "media.segment", "media.importedFromPc", "media.removed", "media.recovery");

    private readonly object gate = new();
    private readonly NormalizationOptions options;
    private readonly Dictionary<EventId, RememberedEvent> remembered = new();
    private readonly Queue<EventId> rememberedOrder = new();
    private readonly Dictionary<string, ClipboardCandidate> clipboardCandidates = new(KeyComparer);
    private readonly Queue<string> clipboardOrder = new();

    /// <summary>Initializes a normalizer with bounded transient correlation state.</summary>
    public EventNormalizer(NormalizationOptions? options = null)
    {
        this.options = options ?? new NormalizationOptions();
        this.options.Validate();
    }

    /// <inheritdoc />
    public CanonicalEvent? Normalize(SourceEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);

        lock (gate)
        {
            if (remembered.TryGetValue(value.EventId, out var previous))
            {
                if (!previous.Source.Equals(value))
                {
                    throw new NormalizationException($"Source event {value.EventId} was presented with conflicting facts.");
                }

                return previous.Result;
            }

            RemoveExpiredClipboardCandidates(value.Time.RecordedUtc);
            var result = NormalizeNew(value);
            Remember(value, result);
            return result;
        }
    }

    /// <summary>Normalizes one source event while honoring cancellation before any state change.</summary>
    public ValueTask<CanonicalEvent?> NormalizeAsync(SourceEvent value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = Normalize(value);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(result);
    }

    private CanonicalEvent? NormalizeNew(SourceEvent source)
    {
        if (IsReadOnlyObservation(source))
        {
            return null;
        }

        if (source.Origin == EventOrigin.Clipboard)
        {
            RememberClipboardCandidate(source);
            return null;
        }

        var operation = DetermineOperation(source);
        var quality = DetermineQuality(source, operation);
        var properties = SanitizeProperties(source.Properties);
        properties = ApplyClipboardCorrelation(source, operation, properties, out operation);

        return new CanonicalEvent(
            source.EventId,
            source.SchemaVersion,
            operation,
            source.Origin,
            source.VolumeId,
            source.FileId,
            source.ParentFileId,
            source.Name,
            source.OldName,
            source.Metadata,
            source.Time,
            quality,
            source.ProcessInstanceId,
            source.ProcessQuality,
            source.MountSessionId,
            source.OperationCorrelationId,
            properties);
    }

    private static bool IsReadOnlyObservation(SourceEvent source)
    {
        if (source.Origin != EventOrigin.Etw)
        {
            return false;
        }

        if (TryGetProperty(source.Properties, "observation", out var observation) &&
            IsOneOf(observation, "Read", "Open", "Query", "DirectoryEnumeration"))
        {
            return true;
        }

        return source.Hint is null &&
               TryGetProperty(source.Properties, "operation", out var operation) &&
               IsOneOf(operation, "Read", "Open", "Query", "DirectoryEnumeration");
    }

    private static CanonicalOperation DetermineOperation(SourceEvent source)
    {
        if (source.Hint == CanonicalOperation.UnverifiedGap || source.Quality == EventQuality.UnverifiedGap)
        {
            return CanonicalOperation.UnverifiedGap;
        }

        if (source.Origin is EventOrigin.MftReconciliation or EventOrigin.DirectoryReconciliation)
        {
            return CanonicalOperation.ReconciliationDiscovered;
        }

        if (source.Origin is EventOrigin.ShareSnapshot or EventOrigin.ShareChange)
        {
            return CanonicalOperation.ShareChanged;
        }

        if (TryGetProperty(source.Properties, "recycleAction", out var recycleAction))
        {
            if (IsOneOf(recycleAction, "Recycle", "Recycled", "InRecycleBin"))
            {
                return CanonicalOperation.Recycle;
            }

            if (IsOneOf(recycleAction, "Restore", "Restored", "OutOfRecycleBin"))
            {
                return CanonicalOperation.Restore;
            }
        }

        if (source.Metadata?.InRecycleBin == true ||
            (TryGetProperty(source.Properties, "isRecycleBin", out var inRecycleBin) && IsTrue(inRecycleBin)))
        {
            return CanonicalOperation.Recycle;
        }

        if (source.Metadata?.CloudPlaceholderState is not null ||
            TryGetProperty(source.Properties, "cloudState", out _))
        {
            if (source.Hint is null or CanonicalOperation.MetadataChanged)
            {
                return CanonicalOperation.CloudStateChanged;
            }
        }

        if (TryGetProperty(source.Properties, "securityChanged", out var securityChanged) && IsTrue(securityChanged))
        {
            return CanonicalOperation.SecurityMetadataChanged;
        }

        var hinted = source.Hint ?? ParseOperation(source.Properties);
        if (hinted is null)
        {
            return CanonicalOperation.UnverifiedGap;
        }

        if (hinted == CanonicalOperation.Create && IsDirectory(source))
        {
            return CanonicalOperation.DirectoryCreate;
        }

        if (hinted == CanonicalOperation.Rename && HasParentChanged(source))
        {
            return CanonicalOperation.Move;
        }

        return hinted.Value;
    }

    private static CanonicalOperation? ParseOperation(ImmutableDictionary<string, string> properties)
    {
        if (!TryGetProperty(properties, "operation", out var value) ||
            !Enum.TryParse<CanonicalOperation>(value, ignoreCase: true, out var operation))
        {
            return null;
        }

        return operation;
    }

    private static bool IsDirectory(SourceEvent source) =>
        source.Metadata?.Kind == FileKind.Directory ||
        (TryGetProperty(source.Properties, "isRootChange", out var root) && IsTrue(root));

    private static bool HasParentChanged(SourceEvent source)
    {
        if (!TryGetProperty(source.Properties, "oldParentFileId", out var oldParent))
        {
            return false;
        }

        return !string.Equals(oldParent, source.ParentFileId?.Value, StringComparison.Ordinal);
    }

    private static EventQuality DetermineQuality(SourceEvent source, CanonicalOperation operation)
    {
        if (operation == CanonicalOperation.ReconciliationDiscovered)
        {
            return EventQuality.Reconciled;
        }

        if (operation == CanonicalOperation.UnverifiedGap)
        {
            return EventQuality.UnverifiedGap;
        }

        if (source.Metadata is null && source.Origin is EventOrigin.LiveUsn or EventOrigin.RecoveredUsn)
        {
            return source.Quality is EventQuality.Exact or EventQuality.Correlated
                ? EventQuality.JournalOnly
                : source.Quality;
        }

        return source.Quality;
    }

    private ImmutableDictionary<string, string> ApplyClipboardCorrelation(
        SourceEvent source,
        CanonicalOperation operation,
        ImmutableDictionary<string, string> properties,
        out CanonicalOperation resultingOperation)
    {
        resultingOperation = operation;
        if (operation is not (CanonicalOperation.Create or CanonicalOperation.DirectoryCreate))
        {
            return properties;
        }

        if (!TryGetProperty(source.Properties, "clipboardGeneration", out var generation) ||
            !clipboardCandidates.TryGetValue(generation, out var candidate) ||
            !IsValidClipboardMatch(source, candidate))
        {
            return properties;
        }

        var builder = properties.ToBuilder();
        builder["correlationquality"] = EventQuality.Correlated.ToString();
        builder["copyscope"] = IsDirectory(source) ? "DirectoryRoot" : "File";

        if (candidate.SourceFileId is not null)
        {
            builder["copysourcefileid"] = candidate.SourceFileId.Value.Value;
        }

        if (candidate.Intent == ClipboardIntent.Copy)
        {
            builder["copycorrelationquality"] = EventQuality.Correlated.ToString();
            return builder.ToImmutable();
        }

        if (candidate.Intent == ClipboardIntent.Cut &&
            candidate.SourceFileId is not null &&
            source.FileId == candidate.SourceFileId &&
            candidate.SourceVolumeId is not null &&
            source.VolumeId == candidate.SourceVolumeId &&
            HasParentChangedFromClipboard(source, candidate))
        {
            resultingOperation = CanonicalOperation.Move;
            builder["cutcorrelationquality"] = EventQuality.Correlated.ToString();
        }
        else
        {
            builder["cutcorrelationrejected"] = "IdentityOrVolumeNotConfirmed";
        }

        return builder.ToImmutable();
    }

    private bool IsValidClipboardMatch(SourceEvent source, ClipboardCandidate candidate)
    {
        if (source.Time.RecordedUtc - candidate.RecordedUtc > options.ClipboardValidity ||
            candidate.RecordedUtc - source.Time.RecordedUtc > options.ClipboardValidity)
        {
            return false;
        }

        if (candidate.MountSessionId is not null && source.MountSessionId != candidate.MountSessionId)
        {
            return false;
        }

        if (candidate.ProcessInstanceId is not null && source.ProcessInstanceId is not null &&
            candidate.ProcessInstanceId != source.ProcessInstanceId)
        {
            return false;
        }

        return TryGetProperty(source.Properties, "explorerOperation", out var explorerOperation) &&
               IsOneOf(explorerOperation, "Create", "Copy", "Paste");
    }

    private static bool HasParentChangedFromClipboard(SourceEvent source, ClipboardCandidate candidate) =>
        candidate.SourceParentFileId is not null && source.ParentFileId is not null &&
        candidate.SourceParentFileId != source.ParentFileId;

    private void RememberClipboardCandidate(SourceEvent source)
    {
        if (!TryGetProperty(source.Properties, "clipboardGeneration", out var generation) || string.IsNullOrWhiteSpace(generation) ||
            !TryGetProperty(source.Properties, "clipboardIntent", out var intentValue) ||
            !TryParseClipboardIntent(intentValue, out var intent))
        {
            return;
        }

        var candidate = new ClipboardCandidate(
            generation,
            intent,
            source.FileId,
            source.VolumeId,
            source.ParentFileId,
            source.MountSessionId,
            source.ProcessInstanceId,
            source.Time.RecordedUtc);

        if (!clipboardCandidates.ContainsKey(generation))
        {
            clipboardOrder.Enqueue(generation);
        }

        clipboardCandidates[generation] = candidate;
        while (clipboardCandidates.Count > options.MaximumClipboardCandidates && clipboardOrder.TryDequeue(out var oldest))
        {
            clipboardCandidates.Remove(oldest);
        }
    }

    private void RemoveExpiredClipboardCandidates(DateTimeOffset now)
    {
        foreach (var pair in clipboardCandidates.Where(pair => now - pair.Value.RecordedUtc > options.ClipboardValidity || pair.Value.RecordedUtc - now > options.ClipboardValidity).ToArray())
        {
            clipboardCandidates.Remove(pair.Key);
        }

        while (clipboardOrder.Count > 0 && !clipboardCandidates.ContainsKey(clipboardOrder.Peek()))
        {
            clipboardOrder.Dequeue();
        }
    }

    private void Remember(SourceEvent source, CanonicalEvent? result)
    {
        remembered[source.EventId] = new RememberedEvent(source, result);
        rememberedOrder.Enqueue(source.EventId);
        while (remembered.Count > options.MaximumRememberedEvents && rememberedOrder.TryDequeue(out var oldest))
        {
            remembered.Remove(oldest);
        }
    }

    private static ImmutableDictionary<string, string> SanitizeProperties(ImmutableDictionary<string, string> source)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var pair in source.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (SafePropertyNames.Contains(pair.Key) && !IsSensitivePropertyName(pair.Key))
            {
                builder[pair.Key.ToLowerInvariant()] = pair.Value;
            }
        }

        return builder.ToImmutable();
    }

    private static bool IsSensitivePropertyName(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized.Contains("content", StringComparison.Ordinal) || normalized.Contains("hash", StringComparison.Ordinal) ||
               normalized.Contains("mime", StringComparison.Ordinal);
    }

    private static bool TryGetProperty(ImmutableDictionary<string, string> properties, string name, out string value)
    {
        foreach (var pair in properties)
        {
            if (KeyComparer.Equals(pair.Key, name))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool IsOneOf(string value, params string[] expected) => expected.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));

    private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

    private static bool TryParseClipboardIntent(string value, out ClipboardIntent intent)
    {
        if (string.Equals(value, "copy", StringComparison.OrdinalIgnoreCase))
        {
            intent = ClipboardIntent.Copy;
            return true;
        }

        if (string.Equals(value, "cut", StringComparison.OrdinalIgnoreCase))
        {
            intent = ClipboardIntent.Cut;
            return true;
        }

        intent = default;
        return false;
    }

    private sealed record RememberedEvent(SourceEvent Source, CanonicalEvent? Result);

    private sealed record ClipboardCandidate(
        string Generation,
        ClipboardIntent Intent,
        FileId? SourceFileId,
        VolumeId? SourceVolumeId,
        FileId? SourceParentFileId,
        MountSessionId? MountSessionId,
        ProcessInstanceId? ProcessInstanceId,
        DateTimeOffset RecordedUtc);

    private enum ClipboardIntent { Copy, Cut }
}
