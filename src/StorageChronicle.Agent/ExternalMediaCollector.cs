using System.Collections.Immutable;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.ExternalMedia;
using StorageChronicle.Platform.Windows.FileSystem;
using StorageChronicle.Platform.Windows.FileSystem.Volumes;
using StorageChronicle.Settings;

namespace StorageChronicle.Agent;

/// <summary>Provides event-driven external-media arrivals and removals to the Agent.</summary>
public interface IExternalMediaChangeSource : IAsyncDisposable
{
    /// <summary>Reads device changes without polling.</summary>
    IAsyncEnumerable<ExternalMediaChange> ReadChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets a registration failure that requires an unverified-gap record, if any.</summary>
    string? RegistrationFailure => null;
}

/// <summary>Adapts the Windows device-notification monitor to the Agent boundary.</summary>
public sealed class WindowsExternalMediaChangeSource : IExternalMediaChangeSource
{
    private readonly WindowsExternalMediaMonitor monitor;

    /// <summary>Initializes the source using the Configuration Manager notification boundary.</summary>
    public WindowsExternalMediaChangeSource(WindowsExternalMediaMonitor? monitor = null) => this.monitor = monitor ?? new WindowsExternalMediaMonitor();

    /// <inheritdoc />
    public IAsyncEnumerable<ExternalMediaChange> ReadChangesAsync(CancellationToken cancellationToken = default) => monitor.ReadChangesAsync(cancellationToken);

    /// <inheritdoc />
    public string? RegistrationFailure => monitor.RegistrationFailure;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => monitor.DisposeAsync();
}

/// <summary>Records mount sessions and imports only confirmed media mirror history.</summary>
public sealed class WindowsExternalMediaCollector : ISourceEventCollector, IAsyncDisposable
{
    private readonly IExternalMediaChangeSource changes;
    private readonly IVolumeEnumerator volumes;
    private readonly ISettingsStore<MachineSettings> settings;
    private readonly string pcId;
    private readonly string ledgerRoot;
    private readonly MountSessionTracker sessions;
    private readonly IMediaMirrorSessionCoordinator? mirrorCoordinator;
    private readonly Dictionary<VolumeId, (MountSession Session, MediaVolumeDescriptor Volume)> active = new();
    private readonly Dictionary<VolumeId, long> sequences = new();

    /// <summary>Initializes the collector with injectable notification, volume, settings, and clock boundaries.</summary>
    public WindowsExternalMediaCollector(
        IExternalMediaChangeSource changes,
        IVolumeEnumerator volumes,
        ISettingsStore<MachineSettings> settings,
        string pcId,
        string? ledgerRoot = null,
        IMediaClock? clock = null,
        IMediaMirrorSessionCoordinator? mirrorCoordinator = null)
    {
        this.changes = changes ?? throw new ArgumentNullException(nameof(changes));
        this.volumes = volumes ?? throw new ArgumentNullException(nameof(volumes));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.pcId = ValidateIdentity(pcId, nameof(pcId));
        this.ledgerRoot = ledgerRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Storage Chronicle", "history", "media-ledgers");
        sessions = new MountSessionTracker(clock);
        this.mirrorCoordinator = mirrorCoordinator;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceEvent> CollectAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (changes.RegistrationFailure is { } registrationError)
        {
            yield return CreateGap(DateTimeOffset.UtcNow, "External media notification registration failed: " + registrationError);
        }

        await foreach (var change in changes.ReadChangesAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (change.Kind == ExternalMediaChangeKind.ContinuityGap)
            {
                yield return CreateGap(change.OccurredUtc, change.GapReason ?? "External media notification continuity was lost.");
                continue;
            }

            if (change.Kind == ExternalMediaChangeKind.Connected)
            {
                var enumeration = await TryEnumerateAsync(cancellationToken).ConfigureAwait(false);
                if (enumeration.Error is not null)
                {
                    yield return CreateGap(change.OccurredUtc, enumeration.Error);
                    continue;
                }

                foreach (var descriptor in enumeration.Descriptors!.Where(value => value.IsExternal && value.IsDirectoryReadable))
                {
                    if (active.ContainsKey(descriptor.Id)) continue;
                    var media = new MediaVolumeDescriptor(descriptor.Id.Value, descriptor.Id, descriptor.FileSystem, descriptor.IsReadOnly, descriptor.SupportsUsn, descriptor.IsSystem);
                    var assessment = MediaQuality.Assess(media);
                    var session = sessions.Start(descriptor.Id, pcId, ToContinuity(assessment.Quality));
                    active[descriptor.Id] = (session, media);
                    yield return CreateMountEvent(media, session, assessment, change.OccurredUtc, removed: false);

                    if (mirrorCoordinator is not null)
                    {
                        Exception? registrationFailure = null;
                        try { await mirrorCoordinator.RegisterAsync(media, session, cancellationToken).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                        {
                            registrationFailure = exception;
                        }
                        if (registrationFailure is not null) yield return CreateMediaGap(media, change.OccurredUtc, "External media mirror registration failed: " + registrationFailure.Message);
                    }

                    var importedEvents = new List<SourceEvent>();
                    Exception? importFailure = null;
                    try
                    {
                        await foreach (var imported in ImportMirrorAsync(media, session, cancellationToken).ConfigureAwait(false)) importedEvents.Add(imported);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        importFailure = exception;
                    }
                    foreach (var imported in importedEvents) yield return imported;
                    if (importFailure is not null) yield return CreateMediaGap(media, change.OccurredUtc, "External media mirror import failed: " + importFailure.Message);
                }
            }
            else
            {
                foreach (var pair in active.ToArray())
                {
                    if (mirrorCoordinator is not null)
                    {
                        Exception? flushFailure = null;
                        try { await mirrorCoordinator.UnregisterAsync(pair.Key, cancellationToken).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                        {
                            flushFailure = exception;
                        }
                        if (flushFailure is not null) yield return CreateMediaGap(pair.Value.Volume, change.OccurredUtc, "External media mirror flush failed: " + flushFailure.Message);
                    }
                    yield return CreateMountEvent(pair.Value.Volume, pair.Value.Session, new MediaQualityAssessment(MediaHistoryQuality.UnverifiedGap, MediaRecoveryKind.FullReconciliation, pair.Value.Volume.FileSystem, pair.Value.Volume.SupportsUsn, pair.Value.Volume.IsReadOnly, "The external device was removed before continuity was confirmed."), change.OccurredUtc, removed: true);
                    active.Remove(pair.Key);
                }
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => changes.DisposeAsync();

    private async IAsyncEnumerable<SourceEvent> ImportMirrorAsync(MediaVolumeDescriptor media, MountSession session, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var configured = settings.Load().Settings.MediaMirrors.TryGetValue(media.LogicalMediaId, out var mirrorRoot) ? mirrorRoot : null;
        if (string.IsNullOrWhiteSpace(configured)) yield break;
        var configuration = ExternalMediaStore.ValidateMirrorConfiguration(new MediaMirrorConfiguration(true, configured!, media.IsSystemVolume, media.IsBootVolume, media.IsRecoveryVolume, media.IsEfiVolume));
        if (!configuration.IsAllowed || media.IsReadOnly) yield break;

        ExternalMediaStore store;
        try { store = new ExternalMediaStore(configuration.MediaRoot, pcId); }
        catch (Exception) { yield break; }

        var ledgerPath = Path.Combine(ledgerRoot, Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(media.LogicalMediaId)) + ".json");
        var ledgerStore = new MediaImportLedgerStore(ledgerPath);
        var ledger = await ledgerStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var result = await new MediaHistoryImporter().ImportAsync(store, ledger, new MediaOnlyFilter(LogicalMediaId: media.LogicalMediaId), cancellationToken).ConfigureAwait(false);
        var imported = ledger with
        {
            ManifestHashes = ledger.ManifestHashes.Concat(result.ImportedManifestHashes).ToHashSet(StringComparer.OrdinalIgnoreCase),
            SegmentHashes = ledger.SegmentHashes.Concat(result.ImportedSegmentHashes ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase),
            BranchHashes = ledger.BranchHashes.Append(result.Branch.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
        await ledgerStore.SaveAsync(imported, cancellationToken).ConfigureAwait(false);
        foreach (var value in result.Events)
        {
            var sequence = NextSequence(media.VolumeId);
            yield return new SourceEvent(EventId.New(), value.SchemaVersion, EventOrigin.InitialSnapshot, value.VolumeId, value.FileId, value.ParentFileId, value.Name, value.OldName, value.Operation, value.Metadata,
                value.Time with { SourceSequence = sequence, MountSequence = new MountSequence(sequence.Value) }, EventQuality.Reconciled, value.ProcessInstanceId, value.ProcessQuality, session.Id, value.OperationCorrelationId,
                value.Properties.SetItem("media.logicalMediaId", media.LogicalMediaId).SetItem("media.quality", "MirroredFromAnotherPc").SetItem("media.branch", result.Branch.Value));
        }
    }

    private SourceEvent CreateMountEvent(MediaVolumeDescriptor media, MountSession session, MediaQualityAssessment assessment, DateTimeOffset occurredUtc, bool removed)
    {
        var properties = ImmutableDictionary<string, string>.Empty
            .Add("media.logicalMediaId", media.LogicalMediaId)
            .Add("media.quality", assessment.Quality.ToString())
            .Add("media.recovery", assessment.Recovery.ToString())
            .Add("media.removed", removed.ToString());
        return new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.InitialSnapshot, media.VolumeId, null, null, null, null, CanonicalOperation.MountSession, null,
            new EventTime(occurredUtc, occurredUtc.Offset, occurredUtc, DateTimeOffset.UtcNow, NextSequence(media.VolumeId), new MountSequence(session.SegmentReferences.Length > 0 ? session.SegmentReferences.Length : 1)),
            assessment.Quality == MediaHistoryQuality.UnverifiedGap ? EventQuality.UnverifiedGap : EventQuality.Exact, null, ProcessAttributionQuality.Unknown, session.Id, null, properties);
    }

    private SourceEvent CreateGap(DateTimeOffset occurredUtc, string reason) => new(EventId.New(), EventSchemaVersion.Current, EventOrigin.InitialSnapshot, null, null, null, null, null, CanonicalOperation.UnverifiedGap, null,
        new EventTime(occurredUtc, occurredUtc.Offset, occurredUtc, DateTimeOffset.UtcNow, new SourceSequence(1), new MountSequence(1)), EventQuality.UnverifiedGap, null, ProcessAttributionQuality.Unknown, null, null,
        ImmutableDictionary<string, string>.Empty.Add("reconciliationReason", reason));

    private SourceEvent CreateMediaGap(MediaVolumeDescriptor media, DateTimeOffset occurredUtc, string reason) => new(EventId.New(), EventSchemaVersion.Current, EventOrigin.InitialSnapshot, media.VolumeId, null, null, null, null, CanonicalOperation.UnverifiedGap, null,
        new EventTime(occurredUtc, occurredUtc.Offset, occurredUtc, DateTimeOffset.UtcNow, NextSequence(media.VolumeId), new MountSequence(1)), EventQuality.UnverifiedGap, null, ProcessAttributionQuality.Unknown, null, null,
        ImmutableDictionary<string, string>.Empty.Add("reconciliationReason", reason).Add("media.logicalMediaId", media.LogicalMediaId));

    private SourceSequence NextSequence(VolumeId volume)
    {
        var next = sequences.TryGetValue(volume, out var value) ? value + 1 : 1;
        sequences[volume] = next;
        return new SourceSequence(next);
    }

    private async ValueTask<(IReadOnlyList<VolumeDescriptor>? Descriptors, string? Error)> TryEnumerateAsync(CancellationToken cancellationToken)
    {
        try { return (await volumes.EnumerateAsync(cancellationToken).ConfigureAwait(false), null); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, exception.Message);
        }
    }

    private static MonitoringContinuity ToContinuity(MediaHistoryQuality quality) => quality switch
    {
        MediaHistoryQuality.Exact => MonitoringContinuity.Continuous,
        MediaHistoryQuality.UsnRecovered => MonitoringContinuity.JournalRecovered,
        MediaHistoryQuality.ReconciliationRequired => MonitoringContinuity.ReconciledState,
        _ => MonitoringContinuity.UnverifiedGap
    };

    private static string ValidateIdentity(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar)) throw new ArgumentException("The identity must be a single path component.", name);
        return value;
    }
}
