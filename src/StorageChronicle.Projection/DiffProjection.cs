using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Projection;

/// <summary>Builds Live, Period, Point-in-Time, and Replay file-system projections.</summary>
public sealed class DiffProjector
{
    private readonly ProjectionPathResolver pathResolver;

    /// <summary>Creates a diff projector that only consumes recorded event facts.</summary>
    public DiffProjector(ProjectionPathResolver? pathResolver = null) => this.pathResolver = pathResolver ?? new ProjectionPathResolver();

    /// <summary>Projects the requested interval without changing source or canonical events.</summary>
    public DiffProjection Project(ProjectionDocument document, DateTimeOffset? fromUtc, DateTimeOffset toUtc, DiffMode mode, ProjectionFilter filter)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(filter);
        if (toUtc == default) throw new ArgumentException("A diff end time is required.", nameof(toUtc));
        if (fromUtc is { } from && from > toUtc) throw new ArgumentException("Diff start must not be after its end.", nameof(fromUtc));

        var ordered = ProjectionOrdering.Sort(document.CanonicalEvents).ToArray();
        var paths = pathResolver.ResolvePaths(ordered);
        var selected = SelectEvents(ordered, fromUtc, toUtc, mode).ToArray();
        var rows = selected.GroupBy(value => KeyFor(value, paths), StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildRow(group.ToArray(), paths, mode))
            .Where(row => ProjectionFilterEvaluator.Matches(filter, ProjectionFilterEvaluator.ForDiff(row)))
            .ToArray();
        var known = rows.Where(row => !string.Equals(row.DisplayPath, "場所を特定できない項目", StringComparison.Ordinal)).ToArray();
        var unknown = rows.Where(row => string.Equals(row.DisplayPath, "場所を特定できない項目", StringComparison.Ordinal)).ToArray();
        var adjusted = AddDescendantCounts(known);
        var entries = rows.Select(ToDomainEntry).ToArray();
        return new DiffProjection(mode, toUtc, adjusted, unknown, entries);
    }

    private static IEnumerable<CanonicalEvent> SelectEvents(IReadOnlyList<CanonicalEvent> events, DateTimeOffset? fromUtc, DateTimeOffset toUtc, DiffMode mode)
    {
        if (mode == DiffMode.PointInTime) return events.Where(value => value.Time.RecordedUtc <= toUtc);
        var from = fromUtc ?? DateTimeOffset.MinValue;
        return events.Where(value => value.Time.RecordedUtc >= from && value.Time.RecordedUtc <= toUtc);
    }

    private static string KeyFor(CanonicalEvent value, IReadOnlyDictionary<EventId, string?> paths)
    {
        if (value.FileId is { } fileId) return "file:" + fileId.Value;
        return "path:" + (paths.GetValueOrDefault(value.EventId) ?? value.Name ?? value.EventId.ToString());
    }

    private FileDiffProjection BuildRow(IReadOnlyList<CanonicalEvent> events, IReadOnlyDictionary<EventId, string?> paths, DiffMode mode)
    {
        var ordered = events.OrderBy(value => value.Time.RecordedUtc).ThenBy(value => value.Time.SourceSequence.Value).ToArray();
        var first = ordered[0];
        var last = ordered[^1];
        var oldPath = FirstProperty(ordered, ProjectionPropertyNames.OldPath) ?? PathBefore(ordered, paths);
        var newPath = LastPath(ordered, paths);
        var lastIsDelete = ProjectionOperationRules.IsDeleted(last.Operation);
        var operations = ordered.Select(value => ProjectionOperationRules.ToDiffOperation(value.Operation, value)).ToArray();
        var primary = lastIsDelete
            ? (last.Operation == CanonicalOperation.Recycle ? DiffPrimaryOperation.Recycle : DiffPrimaryOperation.Delete)
            : SelectPrimary(operations);
        var subOperations = operations.Where(operation => operation != primary).Distinct().ToArray();
        if (primary == DiffPrimaryOperation.MoveTo && oldPath is not null) subOperations = subOperations.Append(DiffPrimaryOperation.MoveFrom).Distinct().ToArray();
        var isDeleted = lastIsDelete;
        var unknownLocation = newPath is null && oldPath is null;
        var displayPath = isDeleted ? oldPath ?? newPath ?? "場所を特定できない項目" : newPath ?? oldPath ?? "場所を特定できない項目";
        var kind = last.Metadata?.Kind ?? first.Metadata?.Kind ?? FileKind.Unknown;
        var relatedId = FirstProperty(ordered, ProjectionPropertyNames.RelatedOperationId) ?? ordered.Select(value => value.OperationCorrelationId).FirstOrDefault(value => value is not null);
        var renameHistory = ordered.Where(value => value.Operation == CanonicalOperation.Rename).Select(value => value.Name ?? value.OldName).Where(value => value is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var timeline = BuildTimeline(ordered, mode);
        var periodOnly = ordered.Any(value => value.Operation is CanonicalOperation.Create or CanonicalOperation.DirectoryCreate) && isDeleted;
        var virtualRow = unknownLocation || isDeleted;
        var canOpen = !virtualRow && newPath is not null;
        return new FileDiffProjection(
            first.FileId,
            last.Name ?? first.Name ?? "不明な項目",
            oldPath,
            newPath,
            displayPath,
            kind,
            primary,
            ProjectionOperationRules.ToSemanticState(primary),
            subOperations,
            relatedId,
            virtualRow,
            isDeleted,
            periodOnly,
            canOpen,
            canOpen ? null : unknownLocation ? "場所を特定できない項目です。" : "削除済み項目は開けません。",
            0,
            renameHistory,
            timeline,
            ordered.Select(value => value.EventId).ToArray());
    }

    private static DiffPrimaryOperation SelectPrimary(IReadOnlyList<DiffPrimaryOperation> operations)
    {
        foreach (var candidate in new[]
        {
            DiffPrimaryOperation.Delete, DiffPrimaryOperation.Recycle, DiffPrimaryOperation.Restore,
            DiffPrimaryOperation.MoveFrom, DiffPrimaryOperation.MoveTo, DiffPrimaryOperation.Copy,
            DiffPrimaryOperation.Create, DiffPrimaryOperation.Rename, DiffPrimaryOperation.DataWrite,
            DiffPrimaryOperation.Resize, DiffPrimaryOperation.Truncate, DiffPrimaryOperation.Share,
            DiffPrimaryOperation.CloudState, DiffPrimaryOperation.MetadataChange, DiffPrimaryOperation.Unknown
        })
        {
            if (operations.Contains(candidate)) return candidate;
        }

        return DiffPrimaryOperation.Unknown;
    }

    private static ReplayTimelinePoint[] BuildTimeline(IReadOnlyList<CanonicalEvent> events, DiffMode mode)
    {
        if (mode != DiffMode.Replay) return Array.Empty<ReplayTimelinePoint>();
        return events.Select((value, index) => new ReplayTimelinePoint(
            value.EventId,
            value.Time.RecordedUtc,
            ProjectionOperationRules.IsDeleted(value.Operation) ? PaneLifecycle.Ended : index == 0 ? PaneLifecycle.Appeared : PaneLifecycle.Updated,
            ProjectionOperationRules.ToDiffOperation(value.Operation, value))).ToArray();
    }

    private FileDiffProjection[] AddDescendantCounts(IReadOnlyList<FileDiffProjection> rows)
    {
        return rows.Select(row => row with
        {
            DescendantCount = row.Kind == FileKind.Directory
                ? rows.Count(candidate => !ReferenceEquals(candidate, row) && ProjectionPathResolver.IsSameOrDescendant(row.DisplayPath, candidate.DisplayPath))
                : 0
        }).ToArray();
    }

    private static DiffEntry ToDomainEntry(FileDiffProjection row) => new(
        row.FileId,
        row.OldPath,
        row.NewPath,
        ToCanonicalOperation(row.PrimaryOperation),
        row.Kind,
        EventQuality.Exact,
        row.IsVirtual);

    private static CanonicalOperation ToCanonicalOperation(DiffPrimaryOperation operation) => operation switch
    {
        DiffPrimaryOperation.Delete => CanonicalOperation.Delete,
        DiffPrimaryOperation.Recycle => CanonicalOperation.Recycle,
        DiffPrimaryOperation.Restore => CanonicalOperation.Restore,
        DiffPrimaryOperation.MoveFrom or DiffPrimaryOperation.MoveTo => CanonicalOperation.Move,
        DiffPrimaryOperation.Copy or DiffPrimaryOperation.Create => CanonicalOperation.Create,
        DiffPrimaryOperation.Rename => CanonicalOperation.Rename,
        DiffPrimaryOperation.DataWrite => CanonicalOperation.DataWrite,
        DiffPrimaryOperation.Resize => CanonicalOperation.Extend,
        DiffPrimaryOperation.Truncate => CanonicalOperation.Truncate,
        DiffPrimaryOperation.Share => CanonicalOperation.ShareChanged,
        DiffPrimaryOperation.CloudState => CanonicalOperation.CloudStateChanged,
        _ => CanonicalOperation.MetadataChanged
    };

    private static string? FirstProperty(IEnumerable<CanonicalEvent> events, string key) => events.Select(value => value.Properties.GetValueOrDefault(key)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? LastPath(IReadOnlyList<CanonicalEvent> events, IReadOnlyDictionary<EventId, string?> paths) => events.Reverse().Select(value => paths.GetValueOrDefault(value.EventId)).FirstOrDefault(value => value is not null);

    private static string? PathBefore(IReadOnlyList<CanonicalEvent> events, IReadOnlyDictionary<EventId, string?> paths) => events.Select(value => paths.GetValueOrDefault(value.EventId)).FirstOrDefault(value => value is not null);
}
