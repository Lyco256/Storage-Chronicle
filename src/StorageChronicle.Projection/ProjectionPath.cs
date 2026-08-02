using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Projection;

/// <summary>Resolves paths from recorded properties and parent relationships without touching the file system.</summary>
public sealed class ProjectionPathResolver
{
    /// <summary>Resolves all event paths in deterministic event order.</summary>
    public IReadOnlyDictionary<EventId, string?> ResolvePaths(IEnumerable<CanonicalEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var known = new Dictionary<FileId, string>();
        var result = new Dictionary<EventId, string?>();
        foreach (var value in ProjectionOrdering.Sort(events))
        {
            var path = ResolvePath(value, known);
            result[value.EventId] = path;
            if (value.FileId is { } fileId && path is not null) known[fileId] = path;
        }

        return result;
    }

    /// <summary>Resolves one event path from its recorded path or parent chain.</summary>
    public string? ResolvePath(CanonicalEvent value, IReadOnlyDictionary<FileId, string>? knownPaths = null)
    {
        if (value.Properties.TryGetValue(ProjectionPropertyNames.Path, out var explicitPath)) return Normalize(explicitPath);
        if (value.Metadata is { } metadata && metadata.ParentFileId is { } metadataParent && knownPaths?.TryGetValue(metadataParent, out var metadataParentPath) == true)
        {
            return Combine(metadataParentPath, metadata.Name);
        }
        if (value.ParentFileId is { } parent && knownPaths?.TryGetValue(parent, out var parentPath) == true && value.Name is not null)
        {
            return Combine(parentPath, value.Name);
        }
        if (value.Metadata is { Name: var metadataName } && !string.IsNullOrWhiteSpace(metadataName)) return Normalize(metadataName);
        return value.Name is null ? null : Normalize(value.Name);
    }

    /// <summary>Returns the parent folder used as the initial Activity anchor.</summary>
    public string? ResolveAnchor(CanonicalEvent value, IReadOnlyDictionary<EventId, string?> paths)
    {
        var path = paths.TryGetValue(value.EventId, out var resolved) ? resolved : ResolvePath(value);
        if (value.Properties.TryGetValue("parentPath", out var parentPath)) return Normalize(parentPath);
        return Parent(path);
    }

    /// <summary>Normalizes separators while retaining a root marker.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.Contains("//", StringComparison.Ordinal)) normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }

    /// <summary>Returns a path's parent without reading the operating system.</summary>
    public static string? Parent(string? path)
    {
        var normalized = Normalize(path);
        if (normalized is null) return null;
        var slash = normalized.LastIndexOf('/');
        return slash <= 0 ? normalized[..Math.Min(1, normalized.Length)] : normalized[..slash];
    }

    /// <summary>Returns true when descendant is the same path or below ancestor.</summary>
    public static bool IsSameOrDescendant(string? ancestor, string? descendant)
    {
        var left = Normalize(ancestor);
        var right = Normalize(descendant);
        return left is not null && right is not null &&
               (string.Equals(left, right, StringComparison.OrdinalIgnoreCase) || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Combines two path components using neutral slash semantics.</summary>
    public static string Combine(string parent, string name) => Normalize(parent.TrimEnd('/', '\\') + "/" + name.Trim('/', '\\')) ?? name;
}

internal static class ProjectionOrdering
{
    public static IEnumerable<CanonicalEvent> Sort(IEnumerable<CanonicalEvent> events) => events
        .OrderBy(value => value.Time.RecordedUtc)
        .ThenBy(value => value.Time.SourceSequence.Value)
        .ThenBy(value => value.Time.MountSequence.Value)
        .ThenBy(value => value.EventId.Value);

    public static IEnumerable<SourceEvent> Sort(IEnumerable<SourceEvent> events) => events
        .OrderBy(value => value.Time.RecordedUtc)
        .ThenBy(value => value.Time.SourceSequence.Value)
        .ThenBy(value => value.Time.MountSequence.Value)
        .ThenBy(value => value.EventId.Value);
}
