using System.Collections.Immutable;
using StorageChronicle.Contracts;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.FileSystem.Snapshot;
using StorageChronicle.Platform.Windows.FileSystem.Volumes;
using StorageChronicle.Platform.Windows.Ntfs;
using StorageChronicle.Storage;

namespace StorageChronicle.Agent;

/// <summary>Summarizes one user-confirmed reconciliation without converting a failed run into success.</summary>
public sealed record ReconciliationExecutionSummary(
    string RunId,
    VolumeId VolumeId,
    string FileSystem,
    bool Completed,
    string Status,
    int LightweightEntryCount,
    int CandidateCount,
    int DetailedMetadataQueryCount,
    int DurableEventCount,
    int PrivilegeEnableSuccessCount,
    int PrivilegeFallbackCount,
    ReconciliationPriorityResult Priority,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    string? FailureReason);

/// <summary>Runs the real selected-volume reconciliation requested by the UI.</summary>
public interface IConfirmedReconciliationRunner
{
    /// <summary>Executes one selected pending gap and durably appends discovered facts.</summary>
    ValueTask<ReconciliationExecutionSummary> ExecuteAsync(PendingReconciliationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Coordinates NTFS MFT or non-NTFS snapshot reconciliation without stopping live collection globally.</summary>
public sealed class ConfirmedReconciliationRunner : IConfirmedReconciliationRunner
{
    private readonly IVolumeEnumerator volumes;
    private readonly INtfsApi ntfsApi;
    private readonly WindowsVolumeSnapshotReader snapshotReader;
    private readonly WindowsFileMetadataReader metadataReader;
    private readonly AppendOnlyStorageEngine storage;
    private readonly IEventNormalizer normalizer;
    private readonly AgentHealthState health;
    private readonly ReconciliationLiveEventBuffer liveEvents;

    /// <summary>Initializes the production reconciliation runner.</summary>
    public ConfirmedReconciliationRunner(
        IVolumeEnumerator volumes,
        INtfsApi ntfsApi,
        WindowsVolumeSnapshotReader snapshotReader,
        WindowsFileMetadataReader metadataReader,
        AppendOnlyStorageEngine storage,
        IEventNormalizer normalizer,
        AgentHealthState health,
        ReconciliationLiveEventBuffer? liveEvents = null)
    {
        this.volumes = volumes ?? throw new ArgumentNullException(nameof(volumes));
        this.ntfsApi = ntfsApi ?? throw new ArgumentNullException(nameof(ntfsApi));
        this.snapshotReader = snapshotReader ?? throw new ArgumentNullException(nameof(snapshotReader));
        this.metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        this.health = health ?? throw new ArgumentNullException(nameof(health));
        this.liveEvents = liveEvents ?? new ReconciliationLiveEventBuffer();
    }

    /// <inheritdoc />
    public async ValueTask<ReconciliationExecutionSummary> ExecuteAsync(PendingReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.VolumeId is not { } requestedVolume)
        {
            throw new InvalidOperationException("A confirmed reconciliation must identify one volume.");
        }

        var started = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");
        var descriptor = (await volumes.EnumerateAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(value => value.Id == requestedVolume);
        var failureDescriptor = descriptor ?? new VolumeDescriptor(requestedVolume, request.FileSystem, [], false, true, false, false, false);

        var metrics = new ReconciliationScopeMetrics();
        var sourceSequence = Math.Max(storage.Status.LastSourceSequence + 1, request.SourceSequence.GetValueOrDefault() + 1);
        var uncertainFrom = request.DiscoveredUtc;
        using var liveSession = liveEvents.Begin(requestedVolume, request.SourceSequence.GetValueOrDefault());
        try
        {
            if (descriptor is null)
            {
                throw new IOException($"The requested volume is no longer available: {requestedVolume.Value}.");
            }

            var saved = await ReadSavedEntriesAsync(requestedVolume, cancellationToken).ConfigureAwait(false);
            if (string.Equals(descriptor.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) && descriptor.SupportsUsn)
            {
                var summary = await ExecuteNtfsAsync(descriptor, saved, runId, uncertainFrom, sourceSequence, metrics, cancellationToken).ConfigureAwait(false);
                return await FinishWithLiveEventsAsync(summary with { StartedUtc = started, FinishedUtc = DateTimeOffset.UtcNow }, liveSession, descriptor, uncertainFrom, sourceSequence, cancellationToken).ConfigureAwait(false);
            }

            var nonNtfs = await ExecuteDirectoryAsync(descriptor, saved, runId, uncertainFrom, sourceSequence, metrics, cancellationToken).ConfigureAwait(false);
            return await FinishWithLiveEventsAsync(nonNtfs with { StartedUtc = started, FinishedUtc = DateTimeOffset.UtcNow }, liveSession, descriptor, uncertainFrom, sourceSequence, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            var finished = DateTimeOffset.UtcNow;
            await AppendFailureAsync(failureDescriptor, runId, uncertainFrom, finished, sourceSequence, "Interrupted", exception.Message, CancellationToken.None).ConfigureAwait(false);
            return new ReconciliationExecutionSummary(runId, requestedVolume, failureDescriptor.FileSystem, false, "Interrupted", 0, 0, 0, 0, metrics.PrivilegeEnableSuccessCount, metrics.PrivilegeFallbackCount, metrics.Priority, started, finished, exception.Message);
        }
        catch (Exception exception)
        {
            var finished = DateTimeOffset.UtcNow;
            await AppendFailureAsync(failureDescriptor, runId, uncertainFrom, finished, sourceSequence, "Failed", exception.Message, CancellationToken.None).ConfigureAwait(false);
            return new ReconciliationExecutionSummary(runId, requestedVolume, failureDescriptor.FileSystem, false, "Failed", 0, 0, 0, 0, metrics.PrivilegeEnableSuccessCount, metrics.PrivilegeFallbackCount, metrics.Priority, started, finished, exception.Message);
        }
    }

    private async ValueTask<ReconciliationExecutionSummary> FinishWithLiveEventsAsync(
        ReconciliationExecutionSummary summary,
        ReconciliationLiveEventBuffer.ReconciliationLiveEventSession liveSession,
        VolumeDescriptor volume,
        DateTimeOffset uncertainFrom,
        long sourceSequence,
        CancellationToken cancellationToken)
    {
        var batch = liveSession.Complete();
        if (batch.Overflowed)
        {
            const string reason = "The bounded live-event reconciliation buffer overflowed; the run was not completed.";
            await AppendFailureAsync(volume, summary.RunId, uncertainFrom, DateTimeOffset.UtcNow, sourceSequence, "Failed", reason, CancellationToken.None).ConfigureAwait(false);
            return summary with { Completed = false, Status = "Failed", FinishedUtc = DateTimeOffset.UtcNow, FailureReason = reason };
        }

        foreach (var source in batch.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canonical = normalizer.Normalize(source);
            if (canonical is not null) await storage.ApplyAsync(canonical, cancellationToken).ConfigureAwait(false);
        }

        return summary;
    }

    private async ValueTask<ReconciliationExecutionSummary> ExecuteNtfsAsync(
        VolumeDescriptor volume,
        IReadOnlyDictionary<FileId, SavedEntry> saved,
        string runId,
        DateTimeOffset uncertainFrom,
        long sourceSequence,
        ReconciliationScopeMetrics metrics,
        CancellationToken cancellationToken)
    {
        var devicePath = volume.Id.Value.EndsWith('\\') ? volume.Id.Value : volume.Id.Value + "\\";
        var boundary = ReadJournalBoundary(devicePath);
        var current = new Dictionary<FileId, MftEntry>();
        await foreach (var entry in new WindowsMftEnumerator(ntfsApi, devicePath).EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            current[entry.ToFileId()] = entry;
        }

        var candidates = current.Values.Where(entry => IsCandidate(entry, saved)).ToArray();
        var changes = new List<SourceEvent>(candidates.Length + saved.Count);
        var metadataQueries = 0;
        var sequence = sourceSequence;
        var mountRoot = volume.MountPoints.Count > 0 ? volume.MountPoints[0] : devicePath;
        foreach (var entry in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            metadataQueries++;
            var previous = saved.GetValueOrDefault(entry.ToFileId());
            var path = ResolveMftPath(entry, current, mountRoot);
            var metadata = ReadCandidateMetadata(volume, entry, path, metrics, out var metadataQuality);
            var operation = previous is null ? CanonicalOperation.Create : previous.Parent != entry.ToParentFileId() ? CanonicalOperation.Move : CanonicalOperation.Rename;
            if (previous is not null && previous.Parent == entry.ToParentFileId() && string.Equals(previous.Name, entry.Name, StringComparison.Ordinal)) operation = CanonicalOperation.MetadataChanged;
            changes.Add(CreateReconciliationSource(volume, runId, uncertainFrom, DateTimeOffset.UtcNow, sequence++, operation, entry.ToFileId(), entry.ToParentFileId(), entry.Name, previous?.Name, metadata, metadataQuality, entry.Usn));
        }

        foreach (var removed in saved.Values.Where(value => value.Exists && !current.ContainsKey(value.FileId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = removed.Metadata is null ? null : removed.Metadata with { Exists = false, Quality = EventQuality.Reconciled };
            changes.Add(CreateReconciliationSource(volume, runId, uncertainFrom, DateTimeOffset.UtcNow, sequence++, CanonicalOperation.Delete, removed.FileId, removed.Parent, removed.Name, removed.Name, metadata, EventQuality.Reconciled, sequence));
        }

        var durableCount = 0;
        foreach (var source in changes)
        {
            await AppendAsync(source, cancellationToken).ConfigureAwait(false);
            durableCount++;
        }

        var endBoundary = ReadJournalBoundary(devicePath);
        return new ReconciliationExecutionSummary(runId, volume.Id, volume.FileSystem, true, "Completed", current.Count, candidates.Length + saved.Values.Count(value => value.Exists && !current.ContainsKey(value.FileId)), metadataQueries, durableCount, metrics.PrivilegeEnableSuccessCount, metrics.PrivilegeFallbackCount, metrics.Priority, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, endBoundary is null ? "Journal boundary unavailable after scan." : null);
    }

    private async ValueTask<ReconciliationExecutionSummary> ExecuteDirectoryAsync(
        VolumeDescriptor volume,
        IReadOnlyDictionary<FileId, SavedEntry> saved,
        string runId,
        DateTimeOffset uncertainFrom,
        long sourceSequence,
        ReconciliationScopeMetrics metrics,
        CancellationToken cancellationToken)
    {
        var current = new Dictionary<FileId, SourceEvent>();
        await foreach (var source in snapshotReader.ReadInitialSnapshotAsync(volume, cancellationToken).ConfigureAwait(false))
        {
            if (source.Quality == EventQuality.UnverifiedGap || source.Hint == CanonicalOperation.UnverifiedGap)
            {
                var reason = source.Properties.TryGetValue("reason", out var value) ? value : "Directory snapshot reported a continuity gap.";
                throw new IOException(reason);
            }

            if (source.FileId is { } fileId) current[fileId] = source;
        }

        var changes = new List<SourceEvent>();
        var sequence = sourceSequence;
        foreach (var source in current.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.FileId is not { } fileId || !IsDirectoryCandidate(source, saved)) continue;
            var previous = saved.GetValueOrDefault(fileId);
            changes.Add(CreateReconciliationSource(volume, runId, uncertainFrom, DateTimeOffset.UtcNow, sequence++, previous is null ? CanonicalOperation.Create : CanonicalOperation.MetadataChanged, fileId, source.ParentFileId, source.Name, previous?.Name, source.Metadata, source.Quality, source.Time.SourceSequence.Value));
        }

        foreach (var removed in saved.Values.Where(value => value.Exists && !current.ContainsKey(value.FileId)))
        {
            var metadata = removed.Metadata is null ? null : removed.Metadata with { Exists = false, Quality = EventQuality.Reconciled };
            changes.Add(CreateReconciliationSource(volume, runId, uncertainFrom, DateTimeOffset.UtcNow, sequence++, CanonicalOperation.Delete, removed.FileId, removed.Parent, removed.Name, removed.Name, metadata, EventQuality.Reconciled, sequence));
        }

        var durableCount = 0;
        foreach (var source in changes)
        {
            await AppendAsync(source, cancellationToken).ConfigureAwait(false);
            durableCount++;
        }

        return new ReconciliationExecutionSummary(runId, volume.Id, volume.FileSystem, true, "Completed", current.Count, changes.Count, changes.Count(value => value.Metadata is not null), durableCount, metrics.PrivilegeEnableSuccessCount, metrics.PrivilegeFallbackCount, metrics.Priority, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
    }

    private async ValueTask<IReadOnlyDictionary<FileId, SavedEntry>> ReadSavedEntriesAsync(VolumeId volume, CancellationToken cancellationToken)
    {
        var result = new Dictionary<FileId, SavedEntry>();
        var offset = 0;
        while (true)
        {
            var page = await storage.ReadCanonicalPageAsync(offset, 512, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) break;
            foreach (var value in page)
            {
                if (value.VolumeId != volume || value.FileId is not { } fileId) continue;
                result[fileId] = new SavedEntry(fileId, value.ParentFileId, value.Name ?? string.Empty, value.Metadata?.Exists ?? value.Operation is not (CanonicalOperation.Delete or CanonicalOperation.Recycle), value.Metadata, value.Time.SourceSequence.Value);
            }

            offset += page.Count;
            if (page.Count < 512) break;
        }

        return result;
    }

    private async ValueTask AppendAsync(SourceEvent source, CancellationToken cancellationToken)
    {
        var canonical = normalizer.Normalize(source) ?? throw new InvalidDataException("A reconciliation source fact could not be normalized.");
        await storage.AppendSourceAsync(source, cancellationToken).ConfigureAwait(false);
        await storage.AppendCanonicalAsync(canonical, cancellationToken).ConfigureAwait(false);
        await storage.FlushAsync(cancellationToken).ConfigureAwait(false);
        await storage.ApplyAsync(canonical, cancellationToken).ConfigureAwait(false);
        health.Observe(source, createPendingReconciliation: false);
    }

    private async ValueTask AppendFailureAsync(VolumeDescriptor volume, string runId, DateTimeOffset uncertainFrom, DateTimeOffset uncertainTo, long sourceSequence, string status, string reason, CancellationToken cancellationToken)
    {
        var properties = ImmutableDictionary<string, string>.Empty
            .Add("reconciliationRunId", runId)
            .Add("reconciliationStatus", status)
            .Add("reconciliationReason", reason)
            .Add("uncertainFromUtc", uncertainFrom.ToUniversalTime().ToString("O"))
            .Add("uncertainToUtc", uncertainTo.ToUniversalTime().ToString("O"));
        var source = new SourceEvent(EventId.New(), EventSchemaVersion.Current, volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ? EventOrigin.MftReconciliation : EventOrigin.DirectoryReconciliation, volume.Id, null, null, null, null, CanonicalOperation.UnverifiedGap, null,
            new EventTime(uncertainTo, uncertainTo.Offset, null, uncertainTo, new SourceSequence(Math.Max(1, sourceSequence)), new MountSequence(Math.Max(1, sourceSequence))), EventQuality.UnverifiedGap, null, ProcessAttributionQuality.Unknown, null, null, properties);
        var canonical = normalizer.Normalize(source) ?? throw new InvalidDataException("A reconciliation failure could not be normalized.");
        await storage.AppendSourceAsync(source, cancellationToken).ConfigureAwait(false);
        await storage.AppendCanonicalAsync(canonical, cancellationToken).ConfigureAwait(false);
        await storage.FlushAsync(cancellationToken).ConfigureAwait(false);
        await storage.ApplyAsync(canonical, cancellationToken).ConfigureAwait(false);
        health.Observe(source, createPendingReconciliation: true);
    }

    private FileMetadata? ReadCandidateMetadata(VolumeDescriptor volume, MftEntry entry, string path, ReconciliationScopeMetrics metrics, out EventQuality quality)
    {
        CandidateMetadataResult result;
        var priority = WindowsReconciliationPriorityScope.Enter();
        var privilege = WindowsSeBackupPrivilegeScope.Enter();
        try
        {
            try
            {
                if (entry.IsDirectory)
                {
                    using var handle = metadataReader.OpenDirectoryHandle(path);
                    _ = priority.TrySetLowFileIoPriority(handle);
                }

                var value = metadataReader.Read(path, Path.GetDirectoryName(path));
                quality = value.IsAccessDenied ? EventQuality.ExistenceOnly : EventQuality.Reconciled;
                result = new CandidateMetadataResult(new FileMetadata(volume.Id, value.FileId, value.ParentFileId ?? entry.ToParentFileId(), value.Name, value.Kind, value.LogicalSize, value.AllocatedSize, value.CreatedUtc, value.LastAccessUtc, value.LastWriteUtc, value.FileSystemChangeUtc, value.Attributes, value.ReparsePointKind, null, quality, value.Exists, false), quality);
            }
            catch (UnauthorizedAccessException)
            {
                quality = EventQuality.Unknown;
                result = new CandidateMetadataResult(new FileMetadata(volume.Id, entry.ToFileId(), entry.ToParentFileId(), entry.Name, entry.IsDirectory ? FileKind.Directory : FileKind.File, null, null, null, null, null, null, (FileAttributes)entry.FileAttributes, null, null, quality, true, false), quality);
            }
            catch (IOException)
            {
                quality = EventQuality.Unknown;
                result = new CandidateMetadataResult(new FileMetadata(volume.Id, entry.ToFileId(), entry.ToParentFileId(), entry.Name, entry.IsDirectory ? FileKind.Directory : FileKind.File, null, null, null, null, null, null, (FileAttributes)entry.FileAttributes, null, null, quality, true, false), quality);
            }
        }
        finally
        {
            privilege.Dispose();
            priority.Dispose();
        }

        metrics.Record(privilege.Result, priority.Result);
        quality = result.Quality;
        return result.Metadata;
    }

    private static bool IsCandidate(MftEntry entry, IReadOnlyDictionary<FileId, SavedEntry> saved)
    {
        var fileId = entry.ToFileId();
        return !saved.TryGetValue(fileId, out var previous) || !previous.Exists || previous.Parent != entry.ToParentFileId() || !string.Equals(previous.Name, entry.Name, StringComparison.Ordinal) || previous.SourceSequence != entry.Usn;
    }

    private static bool IsDirectoryCandidate(SourceEvent source, IReadOnlyDictionary<FileId, SavedEntry> saved)
    {
        if (source.FileId is not { } fileId) return false;
        if (!saved.TryGetValue(fileId, out var previous) || !previous.Exists) return true;
        return previous.Parent != source.ParentFileId || !string.Equals(previous.Name, source.Name, StringComparison.Ordinal) || MetadataChanged(previous.Metadata, source.Metadata);
    }

    private static bool MetadataChanged(FileMetadata? previous, FileMetadata? current) => previous is null || current is null ||
        previous.LogicalSize != current.LogicalSize || previous.AllocatedSize != current.AllocatedSize || previous.CreatedUtc != current.CreatedUtc ||
        previous.LastAccessUtc != current.LastAccessUtc || previous.LastWriteUtc != current.LastWriteUtc || previous.FileSystemChangeUtc != current.FileSystemChangeUtc ||
        previous.Attributes != current.Attributes || previous.ReparsePointKind != current.ReparsePointKind || previous.CloudPlaceholderState != current.CloudPlaceholderState ||
        previous.Exists != current.Exists;

    private static string ResolveMftPath(MftEntry entry, IReadOnlyDictionary<FileId, MftEntry> current, string root)
    {
        var names = new Stack<string>();
        var cursor = entry;
        var seen = new HashSet<FileId>();
        while (seen.Add(cursor.ToFileId()))
        {
            if (!string.IsNullOrWhiteSpace(cursor.Name)) names.Push(cursor.Name);
            var parentId = cursor.ToParentFileId();
            if (!current.TryGetValue(parentId, out cursor!)) break;
        }

        var path = root;
        foreach (var name in names)
        {
            var combined = Path.GetFullPath(Path.Combine(path, name));
            if (!combined.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return root;
            path = combined;
        }

        return path;
    }

    private static SourceEvent CreateReconciliationSource(VolumeDescriptor volume, string runId, DateTimeOffset uncertainFrom, DateTimeOffset observedAt, long sourceSequence, CanonicalOperation operation, FileId fileId, FileId? parent, string? name, string? oldName, FileMetadata? metadata, EventQuality metadataQuality, long scanSequence)
    {
        var properties = ImmutableDictionary<string, string>.Empty
            .Add("reconciliationRunId", runId)
            .Add("sourceRoute", volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ? "NtfsMftReconciliation" : "DirectorySnapshotReconciliation")
            .Add("uncertainFromUtc", uncertainFrom.ToUniversalTime().ToString("O"))
            .Add("uncertainToUtc", observedAt.ToUniversalTime().ToString("O"))
            .Add("metadataQuality", metadataQuality.ToString())
            .Add("scanSequence", scanSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (oldName is not null) properties = properties.Add("oldName", oldName);
        if (parent is not null) properties = properties.Add("parentFileId", parent.Value.Value);
        return new SourceEvent(EventId.New(), EventSchemaVersion.Current, volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ? EventOrigin.MftReconciliation : EventOrigin.DirectoryReconciliation, volume.Id, fileId, parent, name, oldName, operation, metadata,
            new EventTime(observedAt, observedAt.Offset, null, observedAt, new SourceSequence(Math.Max(1, sourceSequence)), new MountSequence(Math.Max(1, sourceSequence))), EventQuality.Reconciled, null, ProcessAttributionQuality.Unknown, null, runId, properties);
    }

    private UsnJournalState? ReadJournalBoundary(string devicePath)
    {
        try
        {
            using var handle = ntfsApi.OpenVolume(devicePath);
            var result = ntfsApi.QueryUsnJournal(handle, out var data);
            return result.Succeeded && data is not null ? new UsnJournalState(data.JournalId, data.NextUsn) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private sealed record SavedEntry(FileId FileId, FileId? Parent, string Name, bool Exists, FileMetadata? Metadata, long SourceSequence);

    private sealed record CandidateMetadataResult(FileMetadata? Metadata, EventQuality Quality);

    private sealed class ReconciliationScopeMetrics
    {
        private readonly object gate = new();
        private ReconciliationPriorityResult priority = new(false, null, null, 0, 0, 0);

        public int PrivilegeEnableSuccessCount { get; private set; }
        public int PrivilegeFallbackCount { get; private set; }
        public ReconciliationPriorityResult Priority { get { lock (gate) return priority; } }

        public void Record(ReconciliationPrivilegeResult privilege, ReconciliationPriorityResult priorityResult)
        {
            lock (gate)
            {
                if (privilege.Enabled) PrivilegeEnableSuccessCount++;
                else PrivilegeFallbackCount++;
                priority = new(
                    priority.BackgroundModeEnabled || priorityResult.BackgroundModeEnabled,
                    priority.BackgroundStartError ?? priorityResult.BackgroundStartError,
                    priority.BackgroundEndError ?? priorityResult.BackgroundEndError,
                    priority.IoHintAttempts + priorityResult.IoHintAttempts,
                    priority.IoHintSuccesses + priorityResult.IoHintSuccesses,
                    priority.IoHintFailures + priorityResult.IoHintFailures);
            }
        }
    }
}
