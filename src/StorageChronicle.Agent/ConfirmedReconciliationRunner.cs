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
    int AclFallbackCount,
    ReconciliationPriorityResult Priority,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    string? FailureReason)
{
    /// <summary>Gets the explicit detailed-query to candidate ratio for this run.</summary>
    public double DetailedQueryCandidateRatio => CandidateCount == 0 ? 0d : (double)DetailedMetadataQueryCount / CandidateCount;

    /// <summary>Gets the public lightweight MFT entry count recorded for this run.</summary>
    public int MftEntryCount => LightweightEntryCount;

    /// <summary>Gets the privilege-enable failures represented by the fallback count.</summary>
    public int PrivilegeEnableFailureCount => PrivilegeFallbackCount;

    /// <summary>Gets the elapsed wall-clock duration measured by the run boundaries.</summary>
    public double ElapsedMilliseconds => Math.Max(0d, (FinishedUtc - StartedUtc).TotalMilliseconds);

    /// <summary>Gets the NTFS journal boundary captured before the scan, when the volume is NTFS.</summary>
    public UsnJournalState? StartJournalBoundary { get; init; }

    /// <summary>Gets the NTFS journal boundary captured after the scan, when the volume is NTFS.</summary>
    public UsnJournalState? CompletionJournalBoundary { get; init; }

    /// <summary>Gets the timestamp at which the point-in-time scan completed.</summary>
    public DateTimeOffset? ScanCompletedUtc { get; init; }

    /// <summary>Gets the number of live events captured while the scan was active.</summary>
    public int LiveEventCount { get; init; }

    /// <summary>Gets the number of captured live events covered by the scan boundary and not replayed.</summary>
    public int LiveEventsDeduplicated { get; init; }

    /// <summary>Gets the number of captured live events that were already durably committed by the live pipeline.</summary>
    public int LiveEventsAccepted { get; init; }
}

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

        return await Task.Factory.StartNew(
            () => ExecuteOnWorkerAsync(request, requestedVolume, cancellationToken).AsTask(),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap().ConfigureAwait(false);
    }

    private async ValueTask<ReconciliationExecutionSummary> ExecuteOnWorkerAsync(PendingReconciliationRequest request, VolumeId requestedVolume, CancellationToken cancellationToken)
    {

        var started = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");
        var descriptor = (VolumeDescriptor?)null;
        var failureDescriptor = new VolumeDescriptor(requestedVolume, request.FileSystem, [], false, true, false, false, false);

        var metrics = new ReconciliationScopeMetrics();
        var sourceSequence = Math.Max(storage.Status.LastSourceSequence + 1, request.SourceSequence.GetValueOrDefault() + 1);
        var uncertainFrom = request.GapStartUtc ?? request.DiscoveredUtc;
        using var liveSession = liveEvents.Begin(requestedVolume, request.SourceSequence.GetValueOrDefault());
        try
        {
            descriptor = (await volumes.EnumerateAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(value => value.Id == requestedVolume);
            failureDescriptor = descriptor ?? failureDescriptor;
            if (descriptor is null)
            {
                throw new IOException($"The requested volume is no longer available: {requestedVolume.Value}.");
            }

            var saved = await ReadSavedEntriesAsync(requestedVolume, cancellationToken).ConfigureAwait(false);
            if (string.Equals(descriptor.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) && descriptor.SupportsUsn)
            {
                var scan = await ExecuteNtfsAsync(descriptor, saved, runId, uncertainFrom, sourceSequence, metrics, cancellationToken).ConfigureAwait(false);
                return await FinishWithLiveEventsAsync(scan with { Summary = scan.Summary with { StartedUtc = started, FinishedUtc = DateTimeOffset.UtcNow } }, liveSession, descriptor, uncertainFrom, sourceSequence, cancellationToken).ConfigureAwait(false);
            }

            var nonNtfs = await ExecuteDirectoryAsync(descriptor, saved, runId, uncertainFrom, sourceSequence, metrics, cancellationToken).ConfigureAwait(false);
            return await FinishWithLiveEventsAsync(nonNtfs with { Summary = nonNtfs.Summary with { StartedUtc = started, FinishedUtc = DateTimeOffset.UtcNow } }, liveSession, descriptor, uncertainFrom, sourceSequence, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            var finished = DateTimeOffset.UtcNow;
            await AppendFailureAsync(failureDescriptor, runId, uncertainFrom, finished, sourceSequence, "Interrupted", exception.Message, CancellationToken.None).ConfigureAwait(false);
            return new ReconciliationExecutionSummary(runId, requestedVolume, failureDescriptor.FileSystem, false, "Interrupted", 0, 0, 0, 0, metrics.PrivilegeEnableSuccessCount, metrics.PrivilegeFallbackCount, metrics.AclFallbackCount, metrics.Priority, started, finished, exception.Message);
        }
        catch (Exception exception)
        {
            var finished = DateTimeOffset.UtcNow;
            await AppendFailureAsync(failureDescriptor, runId, uncertainFrom, finished, sourceSequence, "Failed", exception.Message, CancellationToken.None).ConfigureAwait(false);
            return new ReconciliationExecutionSummary(runId, requestedVolume, failureDescriptor.FileSystem, false, "Failed", 0, 0, 0, 0, metrics.PrivilegeEnableSuccessCount, metrics.PrivilegeFallbackCount, metrics.AclFallbackCount, metrics.Priority, started, finished, exception.Message);
        }
    }

    private async ValueTask<ReconciliationExecutionSummary> FinishWithLiveEventsAsync(
        ReconciliationScan scan,
        ReconciliationLiveEventBuffer.ReconciliationLiveEventSession liveSession,
        VolumeDescriptor volume,
        DateTimeOffset uncertainFrom,
        long sourceSequence,
        CancellationToken cancellationToken)
    {
        var batch = liveSession.Complete();
        var summary = scan.Summary with
        {
            StartJournalBoundary = scan.StartJournalBoundary,
            CompletionJournalBoundary = scan.CompletionJournalBoundary,
            ScanCompletedUtc = scan.ScanCompletedUtc
        };
        if (batch.Overflowed)
        {
            const string reason = "The bounded live-event reconciliation buffer overflowed; the run was not completed.";
            await AppendFailureAsync(volume, summary.RunId, uncertainFrom, DateTimeOffset.UtcNow, sourceSequence, "Failed", reason, CancellationToken.None).ConfigureAwait(false);
            return summary with { Completed = false, Status = "Failed", FinishedUtc = DateTimeOffset.UtcNow, FailureReason = reason };
        }

        var deduplicated = 0;
        var accepted = 0;
        foreach (var source in batch.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // SourceCommitted is raised only after the live pipeline has appended
            // and applied this source. Re-applying it here would mutate state twice;
            // this pass only classifies the boundary overlap for the durable
            // reconciliation evidence.
            if (IsCoveredByScan(source, scan)) deduplicated++;
            else accepted++;
        }

        return summary with { LiveEventCount = batch.Count, LiveEventsDeduplicated = deduplicated, LiveEventsAccepted = accepted };
    }

    private async ValueTask<ReconciliationScan> ExecuteNtfsAsync(
        VolumeDescriptor volume,
        IReadOnlyDictionary<FileId, SavedEntry> saved,
        string runId,
        DateTimeOffset uncertainFrom,
        long sourceSequence,
        ReconciliationScopeMetrics metrics,
        CancellationToken cancellationToken)
    {
        var devicePath = volume.Id.Value.EndsWith('\\') ? volume.Id.Value : volume.Id.Value + "\\";
        var scanStarted = DateTimeOffset.UtcNow;
        var boundary = ReadJournalBoundary(devicePath);
        if (boundary is null)
        {
            const string reason = "The NTFS journal boundary could not be established before the reconciliation scan.";
            await AppendFailureAsync(volume, runId, uncertainFrom, scanStarted, sourceSequence, "Failed", reason, CancellationToken.None).ConfigureAwait(false);
            return new ReconciliationScan(new ReconciliationExecutionSummary(runId, volume.Id, volume.FileSystem, false, "Failed", 0, 0, 0, 0, metrics.PrivilegeEnableSuccessCount, metrics.PrivilegeFallbackCount, metrics.AclFallbackCount, metrics.Priority, scanStarted, scanStarted, reason), [], scanStarted, null, null);
        }

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
        if (endBoundary is null)
        {
            const string reason = "The NTFS journal boundary could not be established after the reconciliation scan.";
            await AppendFailureAsync(volume, runId, uncertainFrom, DateTimeOffset.UtcNow, sequence, "Failed", reason, CancellationToken.None).ConfigureAwait(false);
            var failed = new ReconciliationExecutionSummary(runId, volume.Id, volume.FileSystem, false, "Failed", current.Count, candidates.Length + saved.Values.Count(value => value.Exists && !current.ContainsKey(value.FileId)), metadataQueries, durableCount, metrics.PrivilegeEnableSuccessCount, metrics.PrivilegeFallbackCount, metrics.AclFallbackCount, metrics.Priority, scanStarted, DateTimeOffset.UtcNow, reason);
            return new ReconciliationScan(failed, changes, DateTimeOffset.UtcNow, boundary, null);
        }

        var completed = DateTimeOffset.UtcNow;
        var summary = new ReconciliationExecutionSummary(runId, volume.Id, volume.FileSystem, true, "Completed", current.Count, candidates.Length + saved.Values.Count(value => value.Exists && !current.ContainsKey(value.FileId)), metadataQueries, durableCount, metrics.PrivilegeEnableSuccessCount, metrics.PrivilegeFallbackCount, metrics.AclFallbackCount, metrics.Priority, scanStarted, completed, null);
        return new ReconciliationScan(summary, changes, completed, boundary, endBoundary);
    }

    private async ValueTask<ReconciliationScan> ExecuteDirectoryAsync(
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

        var completed = DateTimeOffset.UtcNow;
        var summary = new ReconciliationExecutionSummary(runId, volume.Id, volume.FileSystem, true, "Completed", current.Count, changes.Count, changes.Count(value => value.Metadata is not null), durableCount, metrics.PrivilegeEnableSuccessCount, metrics.PrivilegeFallbackCount, metrics.AclFallbackCount, metrics.Priority, completed, completed, null);
        return new ReconciliationScan(summary, changes, completed, null, null);
    }

    private static bool IsCoveredByScan(SourceEvent live, ReconciliationScan scan)
    {
        if (live.FileId is not { } fileId || scan.Sources.Count == 0) return false;
        if (scan.CompletionJournalBoundary is { } boundary && live.Origin is EventOrigin.LiveUsn or EventOrigin.RecoveredUsn && live.Time.SourceSequence.Value >= boundary.NextUsn) return false;
        if (scan.CompletionJournalBoundary is null && scan.ScanCompletedUtc is { } completed && live.Time.RecordedUtc > completed) return false;

        return scan.Sources.Any(value => value.FileId == fileId &&
            (string.Equals(value.Name, live.Name, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value.OldName, live.Name, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value.Name, live.OldName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value.OldName, live.OldName, StringComparison.OrdinalIgnoreCase)));
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
                using var handle = metadataReader.OpenMetadataHandle(path, entry.IsDirectory);
                _ = priority.TrySetLowFileIoPriority(handle);

                var value = metadataReader.Read(path, Path.GetDirectoryName(path));
                quality = value.IsAccessDenied ? EventQuality.ExistenceOnly : EventQuality.Reconciled;
                result = new CandidateMetadataResult(new FileMetadata(volume.Id, value.FileId, value.ParentFileId ?? entry.ToParentFileId(), value.Name, value.Kind, value.LogicalSize, value.AllocatedSize, value.CreatedUtc, value.LastAccessUtc, value.LastWriteUtc, value.FileSystemChangeUtc, value.Attributes, value.ReparsePointKind, null, quality, value.Exists, false), quality, value.IsAccessDenied);
            }
            catch (UnauthorizedAccessException)
            {
                quality = EventQuality.Unknown;
                result = new CandidateMetadataResult(new FileMetadata(volume.Id, entry.ToFileId(), entry.ToParentFileId(), entry.Name, entry.IsDirectory ? FileKind.Directory : FileKind.File, null, null, null, null, null, null, (FileAttributes)entry.FileAttributes, null, null, quality, true, false), quality, true);
            }
            catch (IOException exception)
            {
                quality = EventQuality.Unknown;
                result = new CandidateMetadataResult(new FileMetadata(volume.Id, entry.ToFileId(), entry.ToParentFileId(), entry.Name, entry.IsDirectory ? FileKind.Directory : FileKind.File, null, null, null, null, null, null, (FileAttributes)entry.FileAttributes, null, null, quality, true, false), quality, IsAccessDenied(exception));
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                quality = EventQuality.Unknown;
                result = new CandidateMetadataResult(new FileMetadata(volume.Id, entry.ToFileId(), entry.ToParentFileId(), entry.Name, entry.IsDirectory ? FileKind.Directory : FileKind.File, null, null, null, null, null, null, (FileAttributes)entry.FileAttributes, null, null, quality, true, false), quality, exception.NativeErrorCode == 5);
            }
        }
        finally
        {
            privilege.Dispose();
            priority.Dispose();
        }

        metrics.Record(privilege.Result, priority.Result, result.UsedAclFallback);
        quality = result.Quality;
        return result.Metadata;
    }

    private static bool IsAccessDenied(IOException exception) => (exception.HResult & 0xFFFF) == 5;

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

    private sealed record ReconciliationScan(
        ReconciliationExecutionSummary Summary,
        IReadOnlyList<SourceEvent> Sources,
        DateTimeOffset ScanCompletedUtc,
        UsnJournalState? StartJournalBoundary,
        UsnJournalState? CompletionJournalBoundary);

    private sealed record CandidateMetadataResult(FileMetadata? Metadata, EventQuality Quality, bool UsedAclFallback);

    private sealed class ReconciliationScopeMetrics
    {
        private readonly object gate = new();
        private ReconciliationPriorityResult priority = new(false, null, null, 0, 0, 0);

        public int PrivilegeEnableSuccessCount { get; private set; }
        public int PrivilegeFallbackCount { get; private set; }
        public int AclFallbackCount { get; private set; }
        public ReconciliationPriorityResult Priority { get { lock (gate) return priority; } }

        public void Record(ReconciliationPrivilegeResult privilege, ReconciliationPriorityResult priorityResult, bool aclFallback)
        {
            lock (gate)
            {
                if (privilege.Enabled) PrivilegeEnableSuccessCount++;
                else PrivilegeFallbackCount++;
                if (aclFallback) AclFallbackCount++;
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
