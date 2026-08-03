using StorageChronicle.Contracts;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Projection;
using StorageChronicle.Storage;

namespace StorageChronicle.Agent;

/// <summary>Rebuilds UI projections from the immutable Agent log on demand.</summary>
public sealed class AgentProjectionService : IProjectionService
{
    private readonly AppendOnlyStorageEngine store;
    private readonly ProjectionSettings settings;

    /// <summary>Initializes a projection adapter over one durable store.</summary>
    public AgentProjectionService(AppendOnlyStorageEngine store, ProjectionSettings? settings = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.settings = settings ?? new ProjectionSettings();
    }

    /// <inheritdoc />
    public async ValueTask<ProjectionPage<EventStackRow>> GetEventStackAsync(EventStackMode mode, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var offset = checked((page - 1) * pageSize);
        if (mode == EventStackMode.Source)
        {
            var sources = await store.ReadSourcePageAsync(offset, pageSize, cancellationToken: cancellationToken).ConfigureAwait(false);
            var total = await store.CountEventsAsync(canonical: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            var pageResult = await new ProjectionService(new ProjectionDocument(Array.Empty<CanonicalEvent>(), sources), settings).GetEventStackAsync(mode, 1, pageSize, cancellationToken).ConfigureAwait(false);
            return new ProjectionPage<EventStackRow>(pageResult.Items, page, pageSize, total, offset + pageResult.Items.Count < total);
        }

        if (mode == EventStackMode.Normalized)
        {
            var canonical = await store.ReadCanonicalPageAsync(offset, pageSize, cancellationToken: cancellationToken).ConfigureAwait(false);
            var total = await store.CountEventsAsync(canonical: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            var pageResult = await new ProjectionService(new ProjectionDocument(canonical, Array.Empty<SourceEvent>()), settings).GetEventStackAsync(mode, 1, pageSize, cancellationToken).ConfigureAwait(false);
            return new ProjectionPage<EventStackRow>(pageResult.Items, page, pageSize, total, offset + pageResult.Items.Count < total);
        }

        // Group boundaries are derived from adjacent canonical events. Keep this path
        // regeneration-based until the durable grouped index is built, so no group is
        // silently split at an IPC page boundary.
        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        return await new ProjectionService(document, settings).GetEventStackAsync(mode, page, pageSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads an Event Stack page with the IPC literal search and sort semantics.</summary>
    public async ValueTask<ProjectionPage<EventStackRow>> GetEventStackAsync(ProjectionPageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.PageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.PageSize, 5000);
        if (string.IsNullOrWhiteSpace(request.Filter) && request.Quality is null && request.Origin is null && request.Operation is null && request.AllFilters is not { Count: > 0 } && request.AnyFilters is not { Count: > 0 } && request.ExcludeFilters is not { Count: > 0 })
        {
            return await GetEventStackAsync(request.Mode, request.Page, request.PageSize, cancellationToken).ConfigureAwait(false);
        }
        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var query = CreateQuery(request);
        return await new ProjectionService(document, settings).GetEventStackAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads a bounded recursive Event Stack page for the named-pipe UI.</summary>
    public async ValueTask<ProjectionPageResponse> GetEventStackTreeAsync(ProjectionPageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.PageSize, 1);
        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var tree = await new ProjectionService(document, settings).GetEventStackTreeAsync(CreateQuery(request), cancellationToken).ConfigureAwait(false);
        var nodes = tree.Items.Select(ToSnapshot).ToArray();
        return new ProjectionPageResponse(nodes.Select(value => value.Row).ToArray(), tree.Page, tree.PageSize, tree.TotalCount, tree.HasMore, nodes);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<DiffEntry>> GetDiffAsync(DateTimeOffset? fromUtc, DateTimeOffset toUtc, DiffMode mode, CancellationToken cancellationToken = default)
    {
        if (toUtc == default) throw new ArgumentException("A diff end time is required.", nameof(toUtc));
        if (fromUtc is { } from && from > toUtc) throw new ArgumentException("Diff start must not be after its end.", nameof(fromUtc));
        var canonical = new List<CanonicalEvent>();
        var offset = 0;
        const int batchSize = 2048;
        while (true)
        {
            var batch = await store.ReadCanonicalPageAsync(offset, batchSize, fromUtc, toUtc, cancellationToken).ConfigureAwait(false);
            canonical.AddRange(batch);
            if (batch.Count < batchSize) break;
            offset = checked(offset + batch.Count);
        }

        // Diff projections do not need source facts; keeping the query range bounded
        // avoids loading unrelated source history into the Agent process.
        var document = new ProjectionDocument(canonical, Array.Empty<SourceEvent>());
        return await new ProjectionService(document, settings).GetDiffAsync(fromUtc, toUtc, mode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads the rich Tree/Explorer diff projection for the desktop IPC client.</summary>
    public async ValueTask<DiffProjectionResponse> GetDiffProjectionAsync(DiffProjectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.PageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.PageSize, 5000);
        if (request.ToUtc == default) throw new ArgumentException("A diff end time is required.", nameof(request));
        if (request.FromUtc is { } from && from > request.ToUtc) throw new ArgumentException("Diff start must not be after its end.", nameof(request));
        var canonical = new List<CanonicalEvent>();
        var offset = 0;
        const int batchSize = 2048;
        while (true)
        {
            var batch = await store.ReadCanonicalPageAsync(offset, batchSize, request.FromUtc, request.ToUtc, cancellationToken).ConfigureAwait(false);
            canonical.AddRange(batch);
            if (batch.Count < batchSize) break;
            offset = checked(offset + batch.Count);
        }

        var allTerms = request.AllFilters is { Count: > 0 }
            ? request.AllFilters.Select(value => new FilterTerm(FilterField.Name, value)).ToArray()
            : Array.Empty<FilterTerm>();
        var anyTerms = request.AnyFilters is { Count: > 0 }
            ? request.AnyFilters.Select(value => new FilterTerm(FilterField.Name, value)).ToArray()
            : string.IsNullOrWhiteSpace(request.Filter)
                ? Array.Empty<FilterTerm>()
                : new[] { new FilterTerm(FilterField.Name, request.Filter), new FilterTerm(FilterField.Path, request.Filter), new FilterTerm(FilterField.Operation, request.Filter) };
        var excludedTerms = request.ExcludeFilters is { Count: > 0 }
            ? request.ExcludeFilters.Select(value => new FilterTerm(FilterField.Name, value)).ToArray()
            : Array.Empty<FilterTerm>();
        var filter = new ProjectionFilter(allTerms, anyTerms, excludedTerms);
        var projection = await new ProjectionService(new ProjectionDocument(canonical), settings)
            .GetDiffProjectionAsync(request.FromUtc, request.ToUtc, request.Mode, filter, cancellationToken).ConfigureAwait(false);
        var known = projection.Items.Select(value => (IsUnknown: false, Snapshot: ToSnapshot(value))).ToArray();
        var unknown = projection.UnknownLocationItems.Select(value => (IsUnknown: true, Snapshot: ToSnapshot(value))).ToArray();
        var all = known.Concat(unknown).ToArray();
        var skip = checked((request.Page - 1) * request.PageSize);
        var page = all.Skip(skip).Take(request.PageSize).ToArray();
        var domainPage = projection.DomainEntries.Skip(skip).Take(request.PageSize).ToArray();
        return new DiffProjectionResponse(
            domainPage,
            page.Where(value => !value.IsUnknown).Select(value => value.Snapshot).ToArray(),
            page.Where(value => value.IsUnknown).Select(value => value.Snapshot).ToArray(),
            request.Page,
            request.PageSize,
            all.Length,
            skip + page.Length < all.Length);
    }

    /// <summary>Loads one selected row's recorded times, quality, and process links.</summary>
    public async ValueTask<EventDetailsSnapshot?> GetDetailsAsync(EventId eventId, CancellationToken cancellationToken = default)
    {
        await foreach (var value in store.ReadCanonicalAsync(cancellationToken).ConfigureAwait(false))
        {
            if (value.EventId == eventId) return CreateDetails(value, await ReadCanonicalForChildrenAsync(cancellationToken).ConfigureAwait(false));
        }

        await foreach (var value in store.ReadSourceAsync(cancellationToken).ConfigureAwait(false))
        {
            if (value.EventId == eventId) return CreateDetails(value);
        }

        return null;
    }

    private async ValueTask<ProjectionDocument> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        var canonical = new List<CanonicalEvent>();
        await foreach (var value in store.ReadCanonicalAsync(cancellationToken).ConfigureAwait(false)) canonical.Add(value);
        var source = new List<SourceEvent>();
        await foreach (var value in store.ReadSourceAsync(cancellationToken).ConfigureAwait(false)) source.Add(value);
        return new ProjectionDocument(canonical, source);
    }

    private async ValueTask<IReadOnlyList<CanonicalEvent>> ReadCanonicalForChildrenAsync(CancellationToken cancellationToken)
    {
        var values = new List<CanonicalEvent>();
        await foreach (var value in store.ReadCanonicalAsync(cancellationToken).ConfigureAwait(false)) values.Add(value);
        return values;
    }

    private static EventDetailsSnapshot CreateDetails(CanonicalEvent value, IReadOnlyList<CanonicalEvent>? all = null)
    {
        var processName = GetProperty(value.Properties, "process.name");
        var parent = ParseProcess(GetProperty(value.Properties, "process.parentInstanceId"));
        var children = all is null || value.ProcessInstanceId is not { } process
            ? Array.Empty<ProcessInstanceId>()
            : all.Where(candidate => string.Equals(GetProperty(candidate.Properties, "process.parentInstanceId"), process.Value, StringComparison.Ordinal))
                .Select(candidate => candidate.ProcessInstanceId).Where(candidate => candidate is not null).Select(candidate => candidate!.Value).Distinct().ToArray();
        var row = CreateRow(value.EventId, value.Time, value.Operation, value.Name, value.Quality, value.ProcessInstanceId, value.ProcessQuality, value.Origin, value.Properties);
        return new EventDetailsSnapshot(row, value.Origin, value.Quality, value.Time.RecordedUtc, value.Time.LocalOffset, value.Time.SourceSequence, value.Time.MountSequence,
            string.IsNullOrWhiteSpace(processName) ? null : processName, parent, children,
            value.Operation == CanonicalOperation.ReconciliationDiscovered, value.Operation == CanonicalOperation.UnverifiedGap || value.Quality == EventQuality.UnverifiedGap, value.Quality == EventQuality.ExistenceOnly);
    }

    private static EventDetailsSnapshot CreateDetails(SourceEvent value)
    {
        var operation = value.Hint ?? CanonicalOperation.MetadataChanged;
        var row = CreateRow(value.EventId, value.Time, operation, value.Name, value.Quality, value.ProcessInstanceId, value.ProcessQuality, value.Origin, value.Properties);
        return new EventDetailsSnapshot(row, value.Origin, value.Quality, value.Time.RecordedUtc, value.Time.LocalOffset, value.Time.SourceSequence, value.Time.MountSequence,
            GetProperty(value.Properties, "process.name"), ParseProcess(GetProperty(value.Properties, "process.parentInstanceId")), Array.Empty<ProcessInstanceId>(),
            value.Origin is EventOrigin.MftReconciliation or EventOrigin.DirectoryReconciliation, operation == CanonicalOperation.UnverifiedGap || value.Quality == EventQuality.UnverifiedGap, value.Quality == EventQuality.ExistenceOnly);
    }

    private static EventStackRow CreateRow(EventId id, EventTime time, CanonicalOperation operation, string? name, EventQuality quality, ProcessInstanceId? process, ProcessAttributionQuality processQuality, EventOrigin origin, IReadOnlyDictionary<string, string> properties)
        => new(id, time.RecordedUtc, GetProperty(properties, "path") is { Length: > 0 } path ? path : name ?? "場所を特定できない項目", operation, name ?? operation.ToString(), quality, process, processQuality, origin, Array.Empty<EventId>());

    private static string GetProperty(IReadOnlyDictionary<string, string> properties, string key)
    {
        if (properties.TryGetValue(key, out var value)) return value;
        foreach (var pair in properties)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        }

        return string.Empty;
    }

    private static ProcessInstanceId? ParseProcess(string value) => string.IsNullOrWhiteSpace(value) ? null : ProcessInstanceId.Create(value);

    private static DiffProjectionItemSnapshot ToSnapshot(FileDiffProjection value) => new(
        value.FileId,
        value.DisplayName,
        value.OldPath,
        value.NewPath,
        value.DisplayPath,
        value.Kind,
        value.Quality,
        value.PrimaryOperation.ToString(),
        value.SemanticState.ToString(),
        value.SubOperations.Select(operation => operation.ToString()).ToArray(),
        value.RelatedOperationId,
        value.IsVirtual,
        value.IsDeleted,
        value.IsPeriodOnly,
        value.CanOpenInExplorer,
        value.OpenDisabledReason,
        value.DescendantCount,
        value.RenameHistory,
        value.ReplayTimeline.Select(point => new ReplayTimelinePointSnapshot(point.EventId, point.TimeUtc, point.Lifecycle.ToString(), point.Operation.ToString())).ToArray(),
        value.EventIds);

    private static EventStackQuery CreateQuery(ProjectionPageRequest request)
    {
        var allTerms = new List<FilterTerm>();
        if (request.Quality is { } quality) allTerms.Add(new FilterTerm(FilterField.MonitoringQuality, quality.ToString()));
        if (request.Origin is { } origin) allTerms.Add(new FilterTerm(FilterField.Source, origin.ToString()));
        if (request.Operation is { } operation) allTerms.Add(new FilterTerm(FilterField.Operation, operation.ToString()));
        var anyTerms = string.IsNullOrWhiteSpace(request.Filter)
            ? Array.Empty<FilterTerm>()
            : new[]
            {
                new FilterTerm(FilterField.Name, request.Filter),
                new FilterTerm(FilterField.Path, request.Filter),
                new FilterTerm(FilterField.Operation, request.Filter),
                new FilterTerm(FilterField.Process, request.Filter),
                new FilterTerm(FilterField.Source, request.Filter)
            };
        if (request.AllFilters is { Count: > 0 })
        {
            allTerms.AddRange(request.AllFilters.Select(value => new FilterTerm(FilterField.Name, value)));
        }
        if (request.AnyFilters is { Count: > 0 })
        {
            anyTerms = request.AnyFilters.Select(value => new FilterTerm(FilterField.Name, value)).ToArray();
        }
        var excludedTerms = request.ExcludeFilters is { Count: > 0 }
            ? request.ExcludeFilters.Select(value => new FilterTerm(FilterField.Name, value)).ToArray()
            : Array.Empty<FilterTerm>();
        return new EventStackQuery(request.Mode, request.Page, request.PageSize,
            request.Descending ? ProjectionSortOrder.Descending : ProjectionSortOrder.Ascending,
            new ProjectionFilter(allTerms, anyTerms, excludedTerms));
    }

    private static EventStackNodeSnapshot ToSnapshot(EventStackNode node) => new(
        node.NodeId,
        node.Kind.ToString(),
        node.Row,
        node.ProcessDisplayName,
        node.Children.Select(ToSnapshot).ToArray());
}
