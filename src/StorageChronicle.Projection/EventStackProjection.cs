using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Projection;

/// <summary>Creates Source, Normalized, and Grouped Event Stack projections.</summary>
public sealed class EventStackProjector
{
    private readonly ProjectionPathResolver pathResolver;
    private readonly ActivityGrouper grouper;

    /// <summary>Creates a projector using deterministic path and activity components.</summary>
    public EventStackProjector(ProjectionPathResolver? pathResolver = null)
    {
        this.pathResolver = pathResolver ?? new ProjectionPathResolver();
        grouper = new ActivityGrouper(this.pathResolver);
    }

    /// <summary>Returns root rows for a query. Grouped roots contain all descendants atomically.</summary>
    public ProjectionPage<EventStackRow> GetPage(ProjectionDocument document, EventStackQuery query, ProjectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(settings);
        var rows = GetNodes(document, query.Mode, settings).Where(node => Matches(query.Filter, node)).ToArray();
        var ordered = query.SortOrder == ProjectionSortOrder.Descending
            ? rows.OrderByDescending(node => node.Row.TimeUtc).ThenByDescending(node => node.NodeId, StringComparer.Ordinal).ToArray()
            : rows.OrderBy(node => node.Row.TimeUtc).ThenBy(node => node.NodeId, StringComparer.Ordinal).ToArray();
        var page = ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).Select(node => node.Row).ToArray();
        return new ProjectionPage<EventStackRow>(page, query.Page, query.PageSize, ordered.Length, query.Page * query.PageSize < ordered.Length);
    }

    /// <summary>Returns expandable root nodes while keeping every descendant with its root.</summary>
    public ProjectionPage<EventStackNode> GetTreePage(ProjectionDocument document, EventStackQuery query, ProjectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(settings);
        var nodes = GetNodes(document, query.Mode, settings).Where(node => Matches(query.Filter, node)).ToArray();
        var ordered = query.SortOrder == ProjectionSortOrder.Descending
            ? nodes.OrderByDescending(node => node.Row.TimeUtc).ThenByDescending(node => node.NodeId, StringComparer.Ordinal).ToArray()
            : nodes.OrderBy(node => node.Row.TimeUtc).ThenBy(node => node.NodeId, StringComparer.Ordinal).ToArray();
        var page = ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToArray();
        return new ProjectionPage<EventStackNode>(page, query.Page, query.PageSize, ordered.Length, query.Page * query.PageSize < ordered.Length);
    }

    /// <summary>Builds all root nodes before paging, making regeneration deterministic.</summary>
    public IReadOnlyList<EventStackNode> GetNodes(ProjectionDocument document, EventStackMode mode, ProjectionSettings settings)
    {
        var canonical = ProjectionOrdering.Sort(document.CanonicalEvents).ToArray();
        var sourceByCanonical = BuildSourceIndex(document.SourceEvents);
        return mode switch
        {
            EventStackMode.Source => BuildSourceNodes(document.SourceEvents, sourceByCanonical),
            EventStackMode.Normalized => BuildNormalizedNodes(canonical, sourceByCanonical),
            EventStackMode.Grouped => BuildGroupedNodes(canonical, document.SourceEvents, settings.ActivityTimeout, sourceByCanonical),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private EventStackNode[] BuildSourceNodes(IReadOnlyList<SourceEvent> sources, IReadOnlyDictionary<EventId, IReadOnlyList<SourceEvent>> sourceByCanonical)
    {
        var sorted = ProjectionOrdering.Sort(sources).ToArray();
        var paths = sorted.ToDictionary(value => value.EventId, value => ResolveSourcePath(value));
        return sorted.Select(value =>
        {
            var operation = value.Hint ?? CanonicalOperation.MetadataChanged;
            var row = new EventStackRow(value.EventId, value.Time.RecordedUtc, pathResolver.ResolveAnchor(ToCanonical(value, operation), new Dictionary<EventId, string?> { [value.EventId] = paths[value.EventId] }) ?? "場所を特定できない項目", operation, SourceSummary(value), value.Quality, value.ProcessInstanceId, value.ProcessQuality, value.Origin, Array.Empty<EventId>());
            return new EventStackNode(value.EventId.ToString(), EventStackNodeKind.Source, row, SourceProcessName(value), null, Array.Empty<EventStackNode>());
        }).ToArray();
    }

    private EventStackNode[] BuildNormalizedNodes(IReadOnlyList<CanonicalEvent> canonical, IReadOnlyDictionary<EventId, IReadOnlyList<SourceEvent>> sourceByCanonical)
    {
        var paths = pathResolver.ResolvePaths(canonical);
        var processCatalog = ProcessCatalog.Create(canonical);
        return canonical.Select(value =>
        {
            var row = CanonicalRow(value, paths.GetValueOrDefault(value.EventId), value.EventId.ToString(), value.Operation.ToString());
            var children = sourceByCanonical.TryGetValue(value.EventId, out var sources)
                ? BuildSourceChildren(value.EventId, sources, row.DisplayRoute)
                : Array.Empty<EventStackNode>();
            return new EventStackNode(value.EventId.ToString(), EventStackNodeKind.Normalized, row, processCatalog.For(value)?.DisplayName ?? "不明なプロセス", processCatalog.For(value), children);
        }).ToArray();
    }

    private EventStackNode[] BuildGroupedNodes(IReadOnlyList<CanonicalEvent> canonical, IReadOnlyList<SourceEvent> sources, TimeSpan timeout, IReadOnlyDictionary<EventId, IReadOnlyList<SourceEvent>> sourceByCanonical)
    {
        var paths = pathResolver.ResolvePaths(canonical);
        var processCatalog = ProcessCatalog.Create(canonical);
        return grouper.Group(canonical, timeout).Select(group =>
        {
            var first = group.Events[0];
            var primary = ProjectionOperationRules.Primary(group.Events.Select(value => value.Operation));
            var row = new EventStackRow(first.EventId, group.StartedUtc, group.DisplayRoute, primary, GroupSummary(group), first.Quality, first.ProcessInstanceId, group.ProcessQuality, group.Source, group.Events.Select(value => value.EventId).ToArray());
            var fileChildren = group.Files.Select(file =>
            {
                var fileEvent = group.Events.First(value => file.EventIds.Contains(value.EventId));
                var fileRow = new EventStackRow(fileEvent.EventId, fileEvent.Time.RecordedUtc, file.DisplayPath, file.PrimaryOperation, $"{file.DisplayPath} ({file.OperationCount})", fileEvent.Quality, fileEvent.ProcessInstanceId, fileEvent.ProcessQuality, fileEvent.Origin, file.EventIds);
                var normalizedChildren = file.EventIds.Select(eventId => group.Events.First(value => value.EventId == eventId)).Select(value =>
                {
                    var normalizedRow = CanonicalRow(value, paths.GetValueOrDefault(value.EventId), value.EventId.ToString(), value.Operation.ToString());
                    var sourceChildren = sourceByCanonical.TryGetValue(value.EventId, out var sourceValues)
                        ? BuildSourceChildren(value.EventId, sourceValues, normalizedRow.DisplayRoute)
                        : Array.Empty<EventStackNode>();
                    return new EventStackNode(value.EventId.ToString(), EventStackNodeKind.Normalized, normalizedRow, processCatalog.For(value)?.DisplayName ?? group.ProcessDisplayName, processCatalog.For(value), sourceChildren);
                }).ToArray();
                return new EventStackNode($"{group.GroupId}/file/{fileEvent.EventId}", EventStackNodeKind.FileSummary, fileRow, group.ProcessDisplayName, group.Process, normalizedChildren);
            }).ToArray();
            var kind = group.Metrics.UnverifiedGapCount > 0 || primary == CanonicalOperation.UnverifiedGap ? EventStackNodeKind.Gap : EventStackNodeKind.Activity;
            return new EventStackNode(group.GroupId, kind, row, group.ProcessDisplayName, group.Process, fileChildren);
        }).ToArray();
    }

    private static Dictionary<EventId, IReadOnlyList<SourceEvent>> BuildSourceIndex(IReadOnlyList<SourceEvent> sources)
    {
        var result = new Dictionary<EventId, IReadOnlyList<SourceEvent>>();
        foreach (var source in sources)
        {
            var normalizedId = source.Properties.TryGetValue(ProjectionPropertyNames.NormalizedEventId, out var value) && Guid.TryParse(value, out var guid)
                ? new EventId(guid)
                : source.EventId;
            if (!result.TryGetValue(normalizedId, out var existing)) result[normalizedId] = existing = [];
            result[normalizedId] = existing.Append(source).ToArray();
        }

        return result;
    }

    private EventStackNode[] BuildSourceChildren(EventId normalizedId, IReadOnlyList<SourceEvent> values, string route)
    {
        return ProjectionOrdering.Sort(values).Select(value => new EventStackNode(
            $"{normalizedId}/source/{value.EventId}",
            EventStackNodeKind.Source,
            new EventStackRow(value.EventId, value.Time.RecordedUtc, route, value.Hint ?? CanonicalOperation.MetadataChanged, SourceSummary(value), value.Quality, value.ProcessInstanceId, value.ProcessQuality, value.Origin, Array.Empty<EventId>()),
            SourceProcessName(value),
            null,
            Array.Empty<EventStackNode>())).ToArray();
    }

    private EventStackRow CanonicalRow(CanonicalEvent value, string? path, string id, string summary) => new(
        value.EventId,
        value.Time.RecordedUtc,
        pathResolver.ResolveAnchor(value, new Dictionary<EventId, string?> { [value.EventId] = path }) ?? "場所を特定できない項目",
        value.Operation,
        summary,
        value.Quality,
        value.ProcessInstanceId,
        value.ProcessQuality,
        value.Origin,
        Array.Empty<EventId>());

    private string? ResolveSourcePath(SourceEvent value) => value.Properties.TryGetValue(ProjectionPropertyNames.Path, out var path) ? ProjectionPathResolver.Normalize(path) : value.Name;

    private static string SourceSummary(SourceEvent value) => value.Name ?? value.Hint?.ToString() ?? "Source Event";
    private static string GroupSummary(ActivityGroupProjection group) => $"{group.ProcessDisplayName}: {group.Metrics.OperationCount} operations, {group.Metrics.FileCount} files";
    private static string SourceProcessName(SourceEvent value)
    {
        if (value.Properties.TryGetValue(ProjectionPropertyNames.ProcessName, out var processName)) return processName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase) ? "Explorer操作" : processName;
        return value.ProcessInstanceId is null || value.ProcessQuality == ProcessAttributionQuality.Unknown ? "不明なプロセス" : value.ProcessInstanceId.Value.Value;
    }

    private static CanonicalEvent ToCanonical(SourceEvent value, CanonicalOperation operation) => new(
        value.EventId, value.SchemaVersion, operation, value.Origin, value.VolumeId, value.FileId, value.ParentFileId, value.Name, value.OldName, value.Metadata,
        value.Time, value.Quality, value.ProcessInstanceId, value.ProcessQuality, value.MountSessionId, value.OperationCorrelationId, value.Properties);

    private static bool Matches(ProjectionFilter filter, EventStackNode node)
    {
        var values = new Dictionary<FilterField, string>
        {
            [FilterField.Time] = node.Row.TimeUtc.ToString("O"),
            [FilterField.Path] = node.Row.DisplayRoute,
            [FilterField.Operation] = node.Row.Operation.ToString(),
            [FilterField.Process] = node.ProcessDisplayName,
            [FilterField.Source] = node.Row.Origin.ToString(),
            [FilterField.MonitoringQuality] = node.Row.Quality.ToString(),
            [FilterField.UnknownProcess] = (node.Row.ProcessId is null || node.Row.ProcessQuality == ProcessAttributionQuality.Unknown).ToString(),
            [FilterField.Name] = node.Row.Summary
        };
        var rootMatches = ProjectionFilterEvaluator.Matches(filter, new ProjectionFilterContext(values));
        return rootMatches || node.Children.SelectMany(Descendants).Any(child => Matches(filter, child));
    }

    private static IEnumerable<EventStackNode> Descendants(EventStackNode node)
    {
        yield return node;
        foreach (var child in node.Children.SelectMany(Descendants)) yield return child;
    }
}
