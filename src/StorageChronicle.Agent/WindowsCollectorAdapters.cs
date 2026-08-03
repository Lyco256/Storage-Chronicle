using System.Text.Json;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Abstractions;
using StorageChronicle.Platform.Windows.FileSystem.Volumes;
using StorageChronicle.Platform.Windows.FileSystem.Policy;
using StorageChronicle.Platform.Windows.FileSystem.Snapshot;
using StorageChronicle.Platform.Windows.Ntfs;
using StorageChronicle.Platform.Windows.Session;

namespace StorageChronicle.Agent;

/// <summary>Enumerates NTFS volumes and delegates each one to the real FSCTL USN reader.</summary>
public sealed class WindowsNtfsVolumeCollector : ISourceEventCollector
{
    private readonly IVolumeEnumerator volumes;
    private readonly INtfsApi api;
    private readonly string cursorRoot;
    private readonly WindowsExclusionPolicy? exclusionPolicy;
    private readonly IVolumeSnapshotReader? initialSnapshotReader;

    /// <summary>Initializes an NTFS collector with a durable per-volume cursor directory.</summary>
    public WindowsNtfsVolumeCollector(IVolumeEnumerator volumes, INtfsApi api, string? cursorRoot = null, WindowsExclusionPolicy? exclusionPolicy = null, IVolumeSnapshotReader? initialSnapshotReader = null)
    {
        this.volumes = volumes ?? throw new ArgumentNullException(nameof(volumes));
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        this.cursorRoot = cursorRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Storage Chronicle", "ntfs-cursors");
        this.exclusionPolicy = exclusionPolicy;
        this.initialSnapshotReader = initialSnapshotReader;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceEvent> CollectAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var descriptors = await volumes.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        foreach (var volume in descriptors.Where(value => string.Equals(value.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) && value.SupportsUsn))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var volumeRoot = volume.MountPoints.Count > 0 ? volume.MountPoints[0] : volume.Id.Value;
            if (exclusionPolicy is not null && !exclusionPolicy.IsWholeVolumeMonitored(volumeRoot)) continue;
            var devicePath = volume.Id.Value.EndsWith('\\') ? volume.Id.Value : volume.Id.Value + "\\";
            var previousState = LoadCursor(volume.Id);
            var journalBoundary = previousState;
            if (previousState is null)
            {
                // Capture the journal cursor before the initial enumeration. The reader
                // must subsequently recover records produced while the initial state was
                // being acquired instead of starting at the post-scan journal tail.
                journalBoundary = TryReadJournalBoundary(devicePath);
                if (initialSnapshotReader is null)
                {
                    await foreach (var entry in new WindowsMftEnumerator(api, devicePath).EnumerateAsync(cancellationToken).ConfigureAwait(false))
                    {
                        yield return InitialSnapshot(volume.Id, entry);
                    }
                }
                else
                {
                    // Public MFT enumeration remains the initial identity boundary;
                    // standard metadata is emitted by the bounded directory snapshot
                    // reader so the durable initial state is not an existence-only stub.
                    await foreach (var _ in new WindowsMftEnumerator(api, devicePath).EnumerateAsync(cancellationToken).ConfigureAwait(false)) { }
                    await foreach (var source in initialSnapshotReader.ReadInitialSnapshotAsync(volume, cancellationToken).ConfigureAwait(false))
                    {
                        yield return source;
                    }
                }
            }

            var collector = new WindowsNtfsCollector(volume.Id, devicePath, api, previousState: journalBoundary);
            await foreach (var value in collector.CollectAsync(cancellationToken).ConfigureAwait(false)) yield return value;
            if (collector.LastObservedJournalState is { } state) SaveCursor(volume.Id, state);
        }
    }

    private UsnJournalState? TryReadJournalBoundary(string devicePath)
    {
        try
        {
            using var handle = api.OpenVolume(devicePath);
            var result = api.QueryUsnJournal(handle, out var journal);
            return result.Succeeded && journal is not null ? new UsnJournalState(journal.JournalId, journal.NextUsn) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private static SourceEvent InitialSnapshot(VolumeId volume, MftEntry entry)
    {
        var now = DateTimeOffset.UtcNow;
        var fileId = entry.ToFileId();
        var metadata = new FileMetadata(volume, fileId, entry.ToParentFileId(), entry.Name, entry.IsDirectory ? FileKind.Directory : FileKind.File, null, null, null, null, null, null, (FileAttributes)entry.FileAttributes, null, null, EventQuality.ExistenceOnly, true, false);
        return new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.InitialSnapshot, volume, fileId, entry.ToParentFileId(), entry.Name, null, entry.IsDirectory ? CanonicalOperation.DirectoryCreate : CanonicalOperation.Create, metadata,
            new EventTime(now, now.Offset, null, now, new SourceSequence(entry.Usn), new MountSequence(entry.Usn)), EventQuality.ExistenceOnly, null, ProcessAttributionQuality.Unknown, null, null,
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("sourceRoute", "NtfsMft"));
    }

    private static SourceEvent Gap(VolumeId volume, string reason, long sequence)
    {
        var now = DateTimeOffset.UtcNow;
        return new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.MftReconciliation, volume, null, null, null, null, CanonicalOperation.UnverifiedGap, null,
            new EventTime(now, now.Offset, null, now, new SourceSequence(sequence), new MountSequence(sequence)), EventQuality.UnverifiedGap, null, ProcessAttributionQuality.Unknown, null, null,
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("reconciliationReason", reason));
    }

    private UsnJournalState? LoadCursor(VolumeId volume)
    {
        var path = CursorPath(volume);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<UsnJournalState>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private void SaveCursor(VolumeId volume, UsnJournalState state)
    {
        try
        {
            Directory.CreateDirectory(cursorRoot);
            var path = CursorPath(volume);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state), new System.Text.UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string CursorPath(VolumeId volume) => Path.Combine(cursorRoot, Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(volume.Value)) + ".json");
}

/// <summary>Adapts the hidden-window clipboard source to the Agent collector contract.</summary>
public sealed class WindowsClipboardCollector : ISourceEventCollector, IAsyncDisposable
{
    private readonly ClipboardEventSource source = new(new WindowsClipboardNotificationSource(), new WindowsClipboardReader());
    /// <inheritdoc />
    public IAsyncEnumerable<SourceEvent> CollectAsync(CancellationToken cancellationToken = default) => source.ReadAsync(cancellationToken);
    /// <inheritdoc />
    public ValueTask DisposeAsync() => source.DisposeAsync();
}

/// <summary>Adapts registry-notified local share snapshots to the Agent collector contract.</summary>
public sealed class WindowsShareCollector : ISourceEventCollector, IAsyncDisposable
{
    private readonly WindowsShareStateSource source = new();
    /// <inheritdoc />
    public IAsyncEnumerable<SourceEvent> CollectAsync(CancellationToken cancellationToken = default) => source.ReadChangesAsync(cancellationToken);
    /// <inheritdoc />
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
