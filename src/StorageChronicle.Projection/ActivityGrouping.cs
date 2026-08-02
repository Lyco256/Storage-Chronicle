using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Projection;

/// <summary>Builds deterministic Activity Groups from immutable canonical events.</summary>
public sealed class ActivityGrouper
{
    private readonly ProjectionPathResolver pathResolver;

    /// <summary>Creates a grouper using the path resolver supplied by the application.</summary>
    public ActivityGrouper(ProjectionPathResolver? pathResolver = null) => this.pathResolver = pathResolver ?? new ProjectionPathResolver();

    /// <summary>Groups events without mutating the input sequence or its event objects.</summary>
    public IReadOnlyList<ActivityGroupProjection> Group(IEnumerable<CanonicalEvent> events, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (timeout < TimeSpan.FromSeconds(0.5) || timeout > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(timeout));

        var ordered = ProjectionOrdering.Sort(events).ToArray();
        var paths = pathResolver.ResolvePaths(ordered);
        var processCatalog = ProcessCatalog.Create(ordered);
        var groups = new List<ActivityBuilder>();
        ActivityBuilder? current = null;

        foreach (var value in ordered)
        {
            var route = pathResolver.ResolveAnchor(value, paths);
            var isRead = ProjectionOperationRules.IsRead(value);
            var sameActor = current is not null && current.MatchesActor(value);
            var withinTimeout = current is not null && value.Time.RecordedUtc - current.LastTime <= timeout;
            var compatibleRoute = current is not null && current.CanAcceptRoute(value, route, paths);

            if (current is null)
            {
                current = new ActivityBuilder(value, route);
                continue;
            }

            if (isRead && withinTimeout)
            {
                current.Add(value, route);
                continue;
            }

            var shouldJoin = sameActor && withinTimeout && compatibleRoute &&
                             value.Operation != CanonicalOperation.UnverifiedGap;
            if (shouldJoin)
            {
                current.Add(value, route);
                continue;
            }

            groups.Add(current);
            current = new ActivityBuilder(value, route)
            {
                ClosedByCompetingActivity = !isRead && value.ProcessInstanceId != current.ProcessId
            };
        }

        if (current is not null) groups.Add(current);
        return groups.Select(builder => builder.Build(processCatalog)).ToArray();
    }

    private sealed class ActivityBuilder
    {
        private readonly List<CanonicalEvent> values = [];
        private readonly List<string?> anchors = [];

        public ActivityBuilder(CanonicalEvent first, string? route)
        {
            values.Add(first);
            anchors.Add(route);
            ProcessId = first.ProcessInstanceId;
            ProcessQuality = first.ProcessQuality;
            Source = first.Origin;
            Volume = first.VolumeId;
            MountSession = first.MountSessionId;
        }

        public ProcessInstanceId? ProcessId { get; }
        public ProcessAttributionQuality ProcessQuality { get; }
        public EventOrigin Source { get; }
        public VolumeId? Volume { get; }
        public MountSessionId? MountSession { get; }
        public bool ClosedByCompetingActivity { get; set; }
        public DateTimeOffset LastTime => values[^1].Time.RecordedUtc;

        public bool MatchesActor(CanonicalEvent value)
        {
            if (ProcessQuality == ProcessAttributionQuality.Unknown || ProcessId is null || value.ProcessQuality == ProcessAttributionQuality.Unknown || value.ProcessInstanceId is null)
            {
                return Source == value.Origin && Volume == value.VolumeId && MountSession == value.MountSessionId;
            }

            return ProcessId == value.ProcessInstanceId;
        }

        public bool CanAcceptRoute(CanonicalEvent value, string? route, IReadOnlyDictionary<EventId, string?> paths)
        {
            if (string.Equals(EffectiveRoute(), route, StringComparison.OrdinalIgnoreCase)) return true;
            var newFolderPath = values
                .Where(item => item.Operation == CanonicalOperation.DirectoryCreate || (item.Operation == CanonicalOperation.Create && item.Metadata?.Kind == FileKind.Directory))
                .Select(item => paths.TryGetValue(item.EventId, out var path) ? path : null)
                .FirstOrDefault(path => path is not null);
            return newFolderPath is not null && ProjectionPathResolver.IsSameOrDescendant(newFolderPath, route);
        }

        public void Add(CanonicalEvent value, string? route)
        {
            values.Add(value);
            anchors.Add(route);
        }

        public ActivityGroupProjection Build(ProcessCatalog processCatalog)
        {
            var ordered = values.OrderBy(value => value.Time.RecordedUtc).ThenBy(value => value.Time.SourceSequence.Value).ToArray();
            var displayRoute = DetermineDisplayRoute(ordered, anchors);
            var groupedFiles = ordered.GroupBy(value => value.FileId?.Value ?? "path:" + (value.Name ?? "unknown"), StringComparer.OrdinalIgnoreCase)
                .Select(fileEvents =>
                {
                    var fileValues = fileEvents.ToArray();
                    var path = fileValues.Select(processCatalog.PathFor).FirstOrDefault(candidate => candidate is not null) ?? "場所を特定できない項目";
                    var sizes = fileValues.Select(value => value.Metadata?.LogicalSize).Where(size => size.HasValue).Select(size => size!.Value).ToArray();
                    return new EventStackFileSummary(
                        fileValues[0].FileId,
                        path,
                        ProjectionOperationRules.Primary(fileValues.Select(value => value.Operation)),
                        fileValues.Length,
                        sizes.Length > 1 ? sizes[^1] - sizes[0] : 0,
                        fileValues.Select(value => value.EventId).ToArray());
                }).ToArray();
            var breakdown = ordered.GroupBy(value => value.Operation).ToDictionary(group => group.Key, group => group.Count());
            var sizeValues = ordered.Select(value => value.Metadata?.LogicalSize).Where(size => size.HasValue).Select(size => size!.Value).ToArray();
            var metrics = new ActivityMetrics(
                ordered.Length,
                groupedFiles.Length,
                sizeValues.Length > 1 ? sizeValues[^1] - sizeValues[0] : 0,
                ordered[^1].Time.RecordedUtc - ordered[0].Time.RecordedUtc,
                breakdown,
                ordered.Count(value => value.Operation == CanonicalOperation.UnverifiedGap || value.Quality == EventQuality.UnverifiedGap));
            var process = processCatalog.For(ordered[0]);
            var groupId = ordered[0].EventId.ToString();
            return new ActivityGroupProjection(
                groupId,
                process,
                process?.DisplayName ?? "不明なプロセス",
                ordered[0].ProcessQuality,
                ordered[0].Origin,
                ordered[0].VolumeId,
                ordered[0].MountSessionId,
                displayRoute ?? "場所を特定できない項目",
                ordered[0].Time.RecordedUtc,
                ordered[^1].Time.RecordedUtc,
                ClosedByCompetingActivity,
                metrics,
                groupedFiles,
                ordered);
        }

        private string? EffectiveRoute() => anchors.LastOrDefault(anchor => anchor is not null);

        private static string? DetermineDisplayRoute(CanonicalEvent[] events, IReadOnlyList<string?> anchors)
        {
            var nonNull = anchors.Where(anchor => anchor is not null).Select(anchor => ProjectionPathResolver.Normalize(anchor)!).ToArray();
            if (nonNull.Length == 0) return null;
            var directoryCreate = events.FirstOrDefault(value => value.Operation == CanonicalOperation.DirectoryCreate || (value.Operation == CanonicalOperation.Create && value.Metadata?.Kind == FileKind.Directory));
            if (directoryCreate is not null && directoryCreate.Properties.TryGetValue(ProjectionPropertyNames.Path, out var createdPath))
            {
                var candidate = ProjectionPathResolver.Normalize(createdPath);
                if (candidate is not null && events.Length > 1 && events.Skip(1).All(value =>
                    value.Properties.TryGetValue(ProjectionPropertyNames.Path, out var path) && ProjectionPathResolver.IsSameOrDescendant(candidate, path)))
                {
                    return candidate;
                }
            }

            // Keep the first anchor for sibling folders; never invent a common ancestor.
            return nonNull[0];
        }
    }
}

internal sealed class ProcessCatalog
{
    private readonly IReadOnlyDictionary<ProcessInstanceId, ProcessProjection> processes;
    private readonly IReadOnlyDictionary<EventId, string?> paths;

    private ProcessCatalog(IReadOnlyDictionary<ProcessInstanceId, ProcessProjection> processes, IReadOnlyDictionary<EventId, string?> paths)
    {
        this.processes = processes;
        this.paths = paths;
    }

    public static ProcessCatalog Create(IReadOnlyList<CanonicalEvent> events)
    {
        var ids = events.Where(value => value.ProcessInstanceId is not null).Select(value => value.ProcessInstanceId!.Value).Distinct().ToArray();
        var parentMap = events.Where(value => value.ProcessInstanceId is not null && value.Properties.ContainsKey(ProjectionPropertyNames.ParentProcessInstanceId))
            .GroupBy(value => value.ProcessInstanceId!.Value)
            .ToDictionary(group => group.Key, group => ParseProcessId(group.First().Properties[ProjectionPropertyNames.ParentProcessInstanceId]));
        var children = ids.ToDictionary(id => id, _ => new List<ProcessInstanceId>());
        foreach (var pair in parentMap)
        {
            if (pair.Value is { } parent && children.TryGetValue(parent, out var list)) list.Add(pair.Key);
        }

        var projections = new Dictionary<ProcessInstanceId, ProcessProjection>();
        foreach (var id in ids)
        {
            var value = events.First(candidate => candidate.ProcessInstanceId == id);
            var name = value.ProcessQuality == ProcessAttributionQuality.Unknown
                ? "不明なプロセス"
                : value.Properties.TryGetValue(ProjectionPropertyNames.ProcessName, out var processName) ? processName : id.Value;
            if (value.ProcessQuality != ProcessAttributionQuality.Unknown && name.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) name = "Explorer操作";
            var executable = value.Properties.TryGetValue(ProjectionPropertyNames.ExecutablePath, out var path) ? path : null;
            var ancestors = new List<ProcessInstanceId>();
            var parent = parentMap.GetValueOrDefault(id);
            var seen = new HashSet<ProcessInstanceId>();
            while (parent is { } ancestor && seen.Add(ancestor))
            {
                ancestors.Add(ancestor);
                parent = parentMap.GetValueOrDefault(ancestor);
            }

            projections[id] = new ProcessProjection(id, name, executable, value.ProcessQuality, parentMap.GetValueOrDefault(id), ancestors, children[id]);
        }

        return new ProcessCatalog(projections, new ProjectionPathResolver().ResolvePaths(events));
    }

    public ProcessProjection? For(CanonicalEvent value) => value.ProcessInstanceId is { } id && processes.TryGetValue(id, out var process) ? process : null;

    public string? PathFor(CanonicalEvent value) => paths.TryGetValue(value.EventId, out var path) ? path : null;

    private static ProcessInstanceId? ParseProcessId(string value) => string.IsNullOrWhiteSpace(value) ? null : ProcessInstanceId.Create(value);
}
