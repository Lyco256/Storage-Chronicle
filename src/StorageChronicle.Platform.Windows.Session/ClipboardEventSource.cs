using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Abstractions;

namespace StorageChronicle.Platform.Windows.Session;

/// <summary>Converts event-driven clipboard notifications into bounded SourceEvent candidates.</summary>
public sealed class ClipboardEventSource : IClipboardEventSource, IAsyncDisposable
{
    private readonly IClipboardNotificationSource notifications;
    private readonly IClipboardReader reader;
    private readonly ClipboardIntentTracker tracker;
    private readonly ClipboardLockRetryOptions retryOptions;
    private readonly ISessionClock clock;
    private long sourceSequence;
    private bool disposed;

    /// <summary>Creates a clipboard source from native or test-injected components.</summary>
    public ClipboardEventSource(
        IClipboardNotificationSource notifications,
        IClipboardReader reader,
        ClipboardIntentTracker? tracker = null,
        ClipboardLockRetryOptions? retryOptions = null,
        ISessionClock? clock = null)
    {
        this.notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.clock = clock ?? new SystemSessionClock();
        this.tracker = tracker ?? new ClipboardIntentTracker(this.clock);
        this.retryOptions = (retryOptions ?? ClipboardLockRetryOptions.Default).Validate();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceEvent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await foreach (var notification in notifications.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ReadWithRetryAsync(cancellationToken).ConfigureAwait(false);
            var sequence = checked(++sourceSequence);
            yield return ToSourceEvent(notification, result, sequence);
        }
    }

    /// <summary>Releases the hidden notification window when this source owns it.</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await notifications.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<ClipboardReadResult> ReadWithRetryAsync(CancellationToken cancellationToken)
    {
        ClipboardReadResult result = new(ClipboardReadStatus.Unsupported, null);
        for (var attempt = 0; attempt < retryOptions.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await reader.TryReadAsync(cancellationToken).ConfigureAwait(false);
            if (result.Status != ClipboardReadStatus.Locked || attempt == retryOptions.MaxAttempts - 1)
            {
                return result;
            }

            await Task.Delay(retryOptions.Delay, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private SourceEvent ToSourceEvent(ClipboardNotification notification, ClipboardReadResult result, long sequence)
    {
        var paths = result.Snapshot?.Paths ?? Array.Empty<string>();
        var isRead = result.Status == ClipboardReadStatus.Read && result.Snapshot is not null;
        var quality = isRead ? ClipboardCandidateQuality.ConfirmedIntent : result.Status is ClipboardReadStatus.Empty or ClipboardReadStatus.Unsupported ? ClipboardCandidateQuality.NotIdentified : ClipboardCandidateQuality.SourceUnknown;
        var properties = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        properties["clipboardQuality"] = quality.ToString();
        properties["clipboardStatus"] = result.Status.ToString();
        properties["clipboardGeneration"] = result.Snapshot?.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0";
        properties["clipboardIsCut"] = (result.Snapshot?.IsCut ?? false).ToString();
        properties["clipboardPathCount"] = paths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var index = 0; index < paths.Count; index++)
        {
            properties[$"clipboardPath.{index}"] = paths[index];
        }

        if (result.Snapshot is { } snapshot && isRead)
        {
            _ = tracker.Observe(snapshot.Generation, snapshot.Paths, snapshot.IsCut);
        }

        var recordedUtc = notification.ObservedUtc;
        return new SourceEvent(
            EventId.New(),
            EventSchemaVersion.Current,
            EventOrigin.Clipboard,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            new EventTime(recordedUtc, recordedUtc.Offset, recordedUtc, clock.UtcNow, new SourceSequence(sequence), new MountSequence(sequence)),
            isRead ? EventQuality.Exact : EventQuality.Unknown,
            null,
            ProcessAttributionQuality.Unknown,
            null,
            result.Snapshot?.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            properties.ToImmutable());
    }
}
