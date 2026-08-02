using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Abstractions;

namespace StorageChronicle.Platform.Windows.Session;

/// <summary>Reads one local NetShareEnum snapshot.</summary>
public interface IShareSnapshotReader
{
    /// <summary>Reads all local shares without querying remote users or access history.</summary>
    ValueTask<IReadOnlyList<ShareDescriptor>> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Waits for LanmanServer share configuration changes.</summary>
public interface IShareChangeNotifier : IAsyncDisposable
{
    /// <summary>Gets whether registry notification registration succeeded.</summary>
    bool IsAvailable { get; }

    /// <summary>Waits for one registry change and returns false when registration becomes unavailable.</summary>
    ValueTask<bool> WaitForChangeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads SMB shares at startup and streams snapshot diffs after registry notifications.</summary>
public sealed class WindowsShareStateSource : IShareStateSource, IAsyncDisposable
{
    private static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(30);
    private readonly IShareSnapshotReader snapshotReader;
    private readonly IShareChangeNotifier notifier;
    private readonly ShareSnapshotDiffer differ;
    private readonly ISessionClock clock;
    private readonly TimeSpan fallbackInterval;
    private long sourceSequence;
    private bool disposed;

    /// <summary>Creates a share source with native readers or deterministic test doubles.</summary>
    public WindowsShareStateSource(
        IShareSnapshotReader? snapshotReader = null,
        IShareChangeNotifier? notifier = null,
        ShareSnapshotDiffer? differ = null,
        ISessionClock? clock = null,
        TimeSpan? fallbackInterval = null)
    {
        this.snapshotReader = snapshotReader ?? new NetShareSnapshotReader();
        this.notifier = notifier ?? new RegistryShareChangeNotifier();
        this.differ = differ ?? new ShareSnapshotDiffer();
        this.clock = clock ?? new SystemSessionClock();
        this.fallbackInterval = (fallbackInterval ?? FallbackInterval) >= TimeSpan.Zero ? fallbackInterval ?? FallbackInterval : throw new ArgumentOutOfRangeException(nameof(fallbackInterval));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ShareDescriptor>> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return snapshotReader.ReadAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceEvent> ReadChangesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var previous = await snapshotReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var fallback = !notifier.IsAvailable;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fallback)
            {
                await Task.Delay(fallbackInterval, cancellationToken).ConfigureAwait(false);
            }
            else if (!await notifier.WaitForChangeAsync(cancellationToken).ConfigureAwait(false))
            {
                fallback = true;
                continue;
            }

            var current = await snapshotReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var change in differ.Diff(previous, current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ToSourceEvent(change, fallback, checked(++sourceSequence));
            }

            previous = current;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await notifier.DisposeAsync().ConfigureAwait(false);
    }

    private SourceEvent ToSourceEvent(ShareChange change, bool fallback, long sequence)
    {
        var properties = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        properties["shareChangeKind"] = change.ChangeKind;
        properties["shareQuality"] = fallback ? "FallbackPolling" : "RegistryNotification";
        properties["shareName"] = change.Share.Name;
        properties["shareLocalPath"] = change.Share.LocalPath;
        properties["shareType"] = change.Share.Type;
        properties["shareDescription"] = change.Share.Description ?? string.Empty;
        properties["sharePermissionCount"] = change.Share.Permissions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var index = 0; index < change.Share.Permissions.Count; index++)
        {
            properties[$"sharePermission.{index}"] = change.Share.Permissions[index];
        }

        var now = clock.UtcNow;
        return new SourceEvent(
            EventId.New(),
            EventSchemaVersion.Current,
            EventOrigin.ShareChange,
            null,
            null,
            null,
            change.Share.Name,
            null,
            CanonicalOperation.ShareChanged,
            null,
            new EventTime(now, now.Offset, now, now, new SourceSequence(sequence), new MountSequence(sequence)),
            fallback ? EventQuality.Unknown : EventQuality.Exact,
            null,
            ProcessAttributionQuality.Unknown,
            null,
            change.Share.Name,
            properties.ToImmutable());
    }
}
