using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Projection;

/// <summary>Evaluates the product's literal AND/OR/exclusion filter semantics.</summary>
public sealed class LiteralProjectionFilterMatcher : IProjectionFilterMatcher
{
    /// <summary>Matches a term by case-insensitive literal containment.</summary>
    public bool Matches(FilterTerm term, ProjectionFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(term);
        ArgumentNullException.ThrowIfNull(context);
        if (term.MatchKind != FilterMatchKind.Literal)
        {
            throw new NotSupportedException("Regex matching is reserved for a future matcher.");
        }

        return context.Values.TryGetValue(term.Field, out var value) &&
               value.Contains(term.Value, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Reusable filter evaluator shared by Event Stack and Diff projections.</summary>
public static class ProjectionFilterEvaluator
{
    /// <summary>Returns whether a context satisfies all filter clauses.</summary>
    public static bool Matches(ProjectionFilter filter, ProjectionFilterContext context, IProjectionFilterMatcher? matcher = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(context);
        matcher ??= new LiteralProjectionFilterMatcher();

        if (filter.AllTerms.Any(term => !matcher.Matches(term, context))) return false;
        if (filter.AnyTerms.Count > 0 && !filter.AnyTerms.Any(term => matcher.Matches(term, context))) return false;
        if (filter.ExcludedTerms.Any(term => matcher.Matches(term, context))) return false;
        return true;
    }

    /// <summary>Builds an event context for a canonical event.</summary>
    public static ProjectionFilterContext ForEvent(CanonicalEvent value, string? path, string processName, string parentProcessName)
    {
        var values = new Dictionary<FilterField, string>
        {
            [FilterField.Time] = value.Time.RecordedUtc.ToString("O"),
            [FilterField.Name] = value.Name ?? string.Empty,
            [FilterField.Path] = path ?? string.Empty,
            [FilterField.Extension] = Path.GetExtension(value.Name ?? string.Empty),
            [FilterField.Operation] = value.Operation.ToString(),
            [FilterField.Process] = processName,
            [FilterField.Executable] = GetProperty(value, ProjectionPropertyNames.ExecutablePath),
            [FilterField.ParentProcess] = parentProcessName,
            [FilterField.Source] = value.Origin.ToString(),
            [FilterField.MonitoringQuality] = value.Quality.ToString(),
            [FilterField.MetadataQuality] = value.Metadata?.Quality.ToString() ?? string.Empty,
            [FilterField.Volume] = value.VolumeId?.Value ?? string.Empty,
            [FilterField.Deleted] = IsDeleted(value).ToString(),
            [FilterField.RecycleBin] = (value.Operation == CanonicalOperation.Recycle || value.Metadata?.InRecycleBin == true).ToString(),
            [FilterField.Shared] = (value.Operation == CanonicalOperation.ShareChanged).ToString(),
            [FilterField.FullReconciliationDifference] = (value.Operation == CanonicalOperation.UnverifiedGap || value.Quality == EventQuality.UnverifiedGap).ToString(),
            [FilterField.UnknownProcess] = (value.ProcessInstanceId is null || value.ProcessQuality == ProcessAttributionQuality.Unknown).ToString(),
            [FilterField.Size] = value.Metadata?.LogicalSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
        };
        return new ProjectionFilterContext(values);
    }

    /// <summary>Builds a context from a detailed diff row.</summary>
    public static ProjectionFilterContext ForDiff(FileDiffProjection value)
    {
        var values = new Dictionary<FilterField, string>
        {
            [FilterField.Name] = value.DisplayName,
            [FilterField.Path] = value.DisplayPath,
            [FilterField.Extension] = Path.GetExtension(value.DisplayName),
            [FilterField.Operation] = value.PrimaryOperation.ToString(),
            [FilterField.Deleted] = value.IsDeleted.ToString(),
            [FilterField.FullReconciliationDifference] = value.PrimaryOperation == DiffPrimaryOperation.Unknown ? "True" : "False",
            [FilterField.Size] = string.Empty
        };
        return new ProjectionFilterContext(values);
    }

    private static string GetProperty(CanonicalEvent value, string key) => value.Properties.TryGetValue(key, out var result) ? result : string.Empty;

    private static bool IsDeleted(CanonicalEvent value) => value.Operation is CanonicalOperation.Delete or CanonicalOperation.Recycle;
}
