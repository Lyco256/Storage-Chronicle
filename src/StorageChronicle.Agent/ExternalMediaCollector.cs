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
    private readonly Func<string, MediaImportLedgerStore> ledgerStoreFactory;
    private readonly MountSessionTracker sessions;
    private readonly IMediaMirrorSessionCoordinator? mirrorCoordinator;
    private readonly IVolumeBoundMediaFileSystemFactory? fileSystemFactory;
    private readonly MediaMirrorConsentService? consent;
    private readonly Dictionary<VolumeId, (MountSession Session, MediaVolumeDescriptor Volume)> active = new();
    private readonly Dictionary<VolumeId, long> sequences = new();

    /// <summary>Initializes the collector with injectable notification, volume, settings, and clock boundaries.</summary>
    public WindowsExternalMediaCollector(
        IExternalMediaChangeSource changes,
        IVolumeEnumerator volumes,
        ISettingsStore<MachineSettings> settings,
        string pcId,
        IMediaClock? clock = null,
        IMediaMirrorSessionCoordinator? mirrorCoordinator = null,
        IVolumeBoundMediaFileSystemFactory? fileSystemFactory = null,
        MediaMirrorConsentService? consent = null)
        : this(changes, volumes, settings, pcId, static path => new MediaImportLedgerStore(path), clock, mirrorCoordinator, fileSystemFactory, consent)
    {
    }

    internal WindowsExternalMediaCollector(
        IExternalMediaChangeSource changes,
        IVolumeEnumerator volumes,
        ISettingsStore<MachineSettings> settings,
        string pcId,
        Func<string, MediaImportLedgerStore> ledgerStoreFactory,
        IMediaClock? clock = null,
        IMediaMirrorSessionCoordinator? mirrorCoordinator = null,
        IVolumeBoundMediaFileSystemFactory? fileSystemFactory = null,
        MediaMirrorConsentService? consent = null)
    {
        this.changes = changes ?? throw new ArgumentNullException(nameof(changes));
        this.volumes = volumes ?? throw new ArgumentNullException(nameof(volumes));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.pcId = ValidateIdentity(pcId, nameof(pcId));
        this.ledgerStoreFactory = ledgerStoreFactory ?? throw new ArgumentNullException(nameof(ledgerStoreFactory));
        sessions = new MountSessionTracker(clock);
        this.mirrorCoordinator = mirrorCoordinator;
        this.fileSystemFactory = fileSystemFactory;
        this.consent = consent;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceEvent> CollectAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (changes.RegistrationFailure is { } registrationError)
        {
            yield return CreateGap(DateTimeOffset.UtcNow, "External media notification registration failed: " + registrationError);
        }

        var notificationContinuityLost = false;
        await foreach (var change in ReadChangesWithReconciliationAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (change.Kind == ExternalMediaChangeKind.ContinuityGap)
            {
                notificationContinuityLost = true;
                foreach (var pair in active.ToArray())
                {
                    if (mirrorCoordinator is not null)
                    {
                        Exception? invalidationFailure = null;
                        try { await mirrorCoordinator.InvalidateAsync(pair.Key).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                        {
                            invalidationFailure = exception;
                        }
                        if (invalidationFailure is not null)
                            yield return CreateMediaGap(pair.Value.Volume, change.OccurredUtc, "Uncertain external-media mirror session could not be safely invalidated: " + invalidationFailure.Message);
                    }
                    active.Remove(pair.Key);
                }
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

                var connectedDescriptors = enumeration.Descriptors!.Where(value => value.IsExternal && value.IsDirectoryReadable).ToArray();
                var resumeAfterContinuityGap = notificationContinuityLost;
                foreach (var descriptor in connectedDescriptors)
                {
                    if (active.ContainsKey(descriptor.Id)) continue;
                    var media = new MediaVolumeDescriptor(descriptor.Id.Value, descriptor.Id, descriptor.FileSystem, descriptor.IsReadOnly, descriptor.SupportsUsn, descriptor.ProtectedRoles, descriptor.IsProtectedRoleClassificationComplete)
                    {
                        MountPoints = descriptor.MountPoints
                    };
                    var assessment = MediaQuality.Assess(media);
                    if (resumeAfterContinuityGap)
                        assessment = assessment with { Quality = MediaHistoryQuality.UnverifiedGap, Recovery = MediaRecoveryKind.FullReconciliation, Explanation = "External media notification continuity was lost; this mount was re-enumerated and requires reconciliation." };
                    var continuity = resumeAfterContinuityGap ? MonitoringContinuity.UnverifiedGap : ToContinuity(assessment.Quality);
                    var session = sessions.Start(descriptor.Id, pcId, continuity);
                    active[descriptor.Id] = (session, media);
                    yield return CreateMountEvent(media, session, assessment, change.OccurredUtc, removed: false);

                    var configuredMirror = settings.Load().Settings.MediaMirrors.TryGetValue(media.LogicalMediaId, out var configuredRoot) ? configuredRoot : null;
                    if (!string.IsNullOrWhiteSpace(configuredMirror))
                    {
                        var authorized = false;
                        Exception? consentFailure = null;
                        try
                        {
                            authorized = consent is not null && await consent.AuthorizeAsync(media, configuredMirror, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
                        {
                            consentFailure = exception;
                        }

                        if (consentFailure is not null)
                        {
                            yield return CreateMediaGap(media, change.OccurredUtc, "External media consent preflight failed closed: " + consentFailure.Message);
                            continue;
                        }

                        if (!authorized)
                        {
                            yield return CreateMediaGap(media, change.OccurredUtc, "External media history import and mirror append are blocked until this PC explicitly approves the currently connected medium.");
                            continue;
                        }
                    }

                    if (mirrorCoordinator is not null)
                    {
                        MediaLogDeletionRecovery? recovery = null;
                        Exception? recoveryFailure = null;
                        try { recovery = await RecoverMissingMirrorAsync(media, cancellationToken).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
                        {
                            recoveryFailure = exception;
                        }

                        if (recovery is { LogWasMissing: true }) yield return CreateMediaRecoveryEvent(media, change.OccurredUtc, recovery);
                        if (recoveryFailure is not null)
                        {
                            yield return CreateMediaGap(media, change.OccurredUtc, "External media history recovery failed: " + recoveryFailure.Message);
                            continue;
                        }

                        Exception? registrationFailure = null;
                        try { await mirrorCoordinator.RegisterAsync(media, session, cancellationToken).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
                        {
                            registrationFailure = exception;
                        }
                        if (registrationFailure is not null)
                        {
                            yield return CreateMediaGap(media, change.OccurredUtc, "External media mirror registration failed: " + registrationFailure.Message);
                            continue;
                        }
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
                if (connectedDescriptors.Length > 0) notificationContinuityLost = false;
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

    private async IAsyncEnumerable<ExternalMediaChange> ReadChangesWithReconciliationAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var change in changes.ReadChangesAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return change;
            if (change.Kind == ExternalMediaChangeKind.ContinuityGap)
            {
                // A gap can be the final queued notification while the medium remains mounted.
                // Re-enumerate immediately instead of waiting for a later arrival notification.
                yield return new ExternalMediaChange(ExternalMediaChangeKind.Connected, DateTimeOffset.UtcNow, null, "Re-enumeration after external-media notification continuity gap.");
            }
        }
    }

    private async IAsyncEnumerable<SourceEvent> ImportMirrorAsync(MediaVolumeDescriptor media, MountSession session, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var configured = settings.Load().Settings.MediaMirrors.TryGetValue(media.LogicalMediaId, out var mirrorRoot) ? mirrorRoot : null;
        if (string.IsNullOrWhiteSpace(configured)) yield break;
        if (consent is null || !consent.CanReadExistingHistory(media, configured)) yield break;
        var configuration = ExternalMediaStore.ValidateMirrorConfiguration(new MediaMirrorConfiguration(true, configured!, media.ProtectedRoles, media.IsProtectedRoleClassificationComplete));
        if (!configuration.IsAllowed || media.IsReadOnly) yield break;
        if (fileSystemFactory is null) yield break;

        ExternalMediaStore store;
        IVolumeBoundMediaFileSystem? pendingFileSystem = null;
        try
        {
            var mediaRoot = ExternalMediaStore.ValidateMediaRoot(configuration.MediaRoot, media.MountPoints);
            pendingFileSystem = fileSystemFactory.Open(media.VolumeId);
            if (consent is null || !pendingFileSystem.DirectoryExists(".StorageChronicle") || !consent.HasGrant(media, mediaRoot, pendingFileSystem.GetOwnedProductDirectoryIdentity()))
            {
                pendingFileSystem.Dispose();
                pendingFileSystem = null;
                yield break;
            }
            store = new ExternalMediaStore(mediaRoot, pcId, media.VolumeId, pendingFileSystem, createIfMissing: false);
            pendingFileSystem = null;
        }
        catch (Exception)
        {
            pendingFileSystem?.Dispose();
            yield break;
        }

        using (store)
        {
        var ledgerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Storage Chronicle", "history", "media-ledgers", Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(media.LogicalMediaId)) + ".json");
        var ledgerStore = ledgerStoreFactory(ledgerPath);
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
    }

    private async ValueTask<MediaLogDeletionRecovery?> RecoverMissingMirrorAsync(MediaVolumeDescriptor media, CancellationToken cancellationToken)
    {
        var configured = settings.Load().Settings.MediaMirrors.TryGetValue(media.LogicalMediaId, out var mirrorRoot) ? mirrorRoot : null;
        if (string.IsNullOrWhiteSpace(configured) || media.IsReadOnly) return null;
        if (consent is null || !consent.CanReadExistingHistory(media, configured)) return null;
        var configuration = ExternalMediaStore.ValidateMirrorConfiguration(new MediaMirrorConfiguration(true, configured!, media.ProtectedRoles, media.IsProtectedRoleClassificationComplete));
        if (!configuration.IsAllowed) return null;
        if (fileSystemFactory is null) return null;
        var mediaRoot = ExternalMediaStore.ValidateMediaRoot(configuration.MediaRoot, media.MountPoints);
        IVolumeBoundMediaFileSystem? pendingFileSystem = fileSystemFactory.Open(media.VolumeId);
        try
        {
            if (!pendingFileSystem.DirectoryExists(".StorageChronicle") || !consent.HasGrant(media, mediaRoot, pendingFileSystem.GetOwnedProductDirectoryIdentity())) return null;
            var recovery = MediaRecovery.RecoverDeletedLogAsync(mediaRoot, media.VolumeId, pendingFileSystem, media.LogicalMediaId, pcId, cancellationToken);
            pendingFileSystem = null;
            return await recovery.ConfigureAwait(false);
        }
        finally
        {
            pendingFileSystem?.Dispose();
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

    private SourceEvent CreateMediaRecoveryEvent(MediaVolumeDescriptor media, DateTimeOffset occurredUtc, MediaLogDeletionRecovery recovery)
    {
        var value = CreateMediaGap(media, occurredUtc, "The opted-in Storage Chronicle media history directory was missing; a new branch was started because previous media history is unavailable.");
        return value with { Properties = value.Properties.Add("media.recovery.branch", recovery.Branch.Value).Add("media.recovery.marker", recovery.MarkerPath) };
    }

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
