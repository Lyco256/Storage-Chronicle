using System.Collections.ObjectModel;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Projection;

namespace StorageChronicle.UI.DiffView;

/// <summary>One lazily materialized Tree node shared by the Tree renderer and selection state.</summary>
public sealed class DiffTreeNode
{
    private readonly Func<CancellationToken, ValueTask<IReadOnlyList<DiffTreeNode>>>? childLoader;

    internal DiffTreeNode(string nodeId, FileDiffProjection projection, int depth, Func<CancellationToken, ValueTask<IReadOnlyList<DiffTreeNode>>>? childLoader)
    {
        NodeId = nodeId;
        Projection = projection;
        Depth = depth;
        this.childLoader = childLoader;
    }

    /// <summary>Stable selection identifier.</summary>
    public string NodeId { get; }
    /// <summary>Projection backing this node; virtual folders have a synthetic projection.</summary>
    public FileDiffProjection Projection { get; }
    /// <summary>Tree indentation depth.</summary>
    public int Depth { get; }
    /// <summary>Materialized children; descendants are not loaded before expansion.</summary>
    public ObservableCollection<DiffTreeNode> Children { get; } = [];
    /// <summary>Gets whether the node is expanded.</summary>
    public bool IsExpanded { get; private set; }
    /// <summary>Gets whether child loading has completed.</summary>
    public bool IsChildrenLoaded { get; private set; }
    /// <summary>Gets whether the node has children that can be lazily loaded.</summary>
    public bool HasUnloadedChildren => !IsChildrenLoaded && childLoader is not null;
    /// <summary>Gets whether this is a virtual, deleted, or unknown-location node.</summary>
    public bool IsVirtual => Projection.IsVirtual;
    /// <summary>Gets the gutter markers for this node.</summary>
    public DiffVisualSet Visuals => DiffVisuals.For(Projection);

    /// <summary>Loads direct children once and expands the node.</summary>
    public async ValueTask ExpandAsync(CancellationToken cancellationToken = default)
    {
        if (!IsChildrenLoaded && childLoader is not null)
        {
            var children = await childLoader(cancellationToken).ConfigureAwait(false);
            Children.Clear();
            foreach (var child in children) Children.Add(child);
            IsChildrenLoaded = true;
        }

        IsExpanded = true;
    }

    /// <summary>Collapses the node without discarding its loaded children.</summary>
    public void Collapse() => IsExpanded = false;

    internal static DiffTreeNode Create(string nodeId, FileDiffProjection projection, int depth, Func<CancellationToken, ValueTask<IReadOnlyList<DiffTreeNode>>>? childLoader) => new(nodeId, projection, depth, childLoader);
}

/// <summary>One visible row emitted by the virtualized Tree renderer.</summary>
public sealed record DiffTreeRow(DiffTreeNode Node, int Depth, bool IsExpandable, bool IsExpanded, DiffVisualSet Visuals);

/// <summary>Builds a path tree without reading files or eagerly materializing all descendants.</summary>
internal static class DiffTreeBuilder
{
    public static IReadOnlyList<DiffTreeNode> Build(IReadOnlyList<FileDiffProjection> items, IReadOnlyList<FileDiffProjection> unknownItems)
    {
        var roots = new List<DiffTreeNode>();
        var known = items.Where(item => !string.IsNullOrWhiteSpace(item.DisplayPath)).ToArray();
        foreach (var group in known.GroupBy(RootPath, StringComparer.OrdinalIgnoreCase))
        {
            var rootPath = group.Key;
            var rootProjection = group.FirstOrDefault(item => PathsEqual(item.DisplayPath, rootPath)) ?? VirtualFolder(rootPath);
            roots.Add(CreateNode(rootPath, rootProjection, 0, known));
        }

        if (unknownItems.Count > 0)
        {
            var unknownProjection = VirtualFolder("Unknown location") with { DisplayName = "Unknown location", Kind = FileKind.Unknown, Quality = EventQuality.Unknown };
            roots.Add(DiffTreeNode.Create("unknown-location", unknownProjection, 0, _ => ValueTask.FromResult<IReadOnlyList<DiffTreeNode>>(unknownItems.Select(item => DiffTreeNode.Create(RowId(item), item, 1, null)).ToArray())));
        }

        return roots.OrderBy(node => node.Projection.DisplayPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static DiffTreeNode CreateNode(string path, FileDiffProjection projection, int depth, IReadOnlyList<FileDiffProjection> all)
    {
        var hasChildren = all.Any(item => IsDescendant(path, item.DisplayPath));
        return DiffTreeNode.Create(path, projection, depth, hasChildren ? _ => ValueTask.FromResult(BuildChildren(path, depth, all)) : null);
    }

    private static IReadOnlyList<DiffTreeNode> BuildChildren(string parentPath, int depth, IReadOnlyList<FileDiffProjection> all)
    {
        var candidates = all.Where(item => IsDescendant(parentPath, item.DisplayPath)).ToArray();
        var groups = candidates.GroupBy(item => ChildPath(parentPath, item.DisplayPath), StringComparer.OrdinalIgnoreCase);
        return groups.Where(group => group.Key is not null).Select(group =>
        {
            var childPath = group.Key!;
            var exact = group.FirstOrDefault(item => PathsEqual(item.DisplayPath, childPath)) ?? VirtualFolder(childPath);
            return CreateNode(childPath, exact, depth + 1, all);
        }).OrderBy(node => node.Projection.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string RootPath(FileDiffProjection item)
    {
        var path = ProjectionPathResolver.Normalize(item.DisplayPath) ?? "Unknown location";
        var slash = path.IndexOf('/');
        return slash > 0 ? path[..slash] : slash == 0 ? "/" : path;
    }

    private static string? ChildPath(string parent, string path)
    {
        var normalizedParent = ProjectionPathResolver.Normalize(parent);
        var normalizedPath = ProjectionPathResolver.Normalize(path);
        if (normalizedParent is null || normalizedPath is null || !IsDescendant(normalizedParent, normalizedPath)) return null;
        var remainder = normalizedParent == "/" ? normalizedPath.TrimStart('/') : normalizedPath[(normalizedParent.Length + 1)..];
        var slash = remainder.IndexOf('/');
        var segment = slash >= 0 ? remainder[..slash] : remainder;
        return ProjectionPathResolver.Combine(normalizedParent, segment);
    }

    private static bool IsDescendant(string parent, string? child)
    {
        var normalizedParent = ProjectionPathResolver.Normalize(parent);
        var normalizedChild = ProjectionPathResolver.Normalize(child);
        var isBelow = normalizedParent == "/"
            ? normalizedChild is not null && normalizedChild.StartsWith('/') && normalizedChild.Length > 1
            : ProjectionPathResolver.IsSameOrDescendant(normalizedParent, normalizedChild);
        return isBelow && !PathsEqual(normalizedParent, normalizedChild);
    }
    private static bool PathsEqual(string? left, string? right) => string.Equals(ProjectionPathResolver.Normalize(left), ProjectionPathResolver.Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string RowId(FileDiffProjection item) => item.FileId is { } id ? "file:" + id.Value : "path:" + item.DisplayPath;

    private static FileDiffProjection VirtualFolder(string path) => new(null, Path.GetFileName(path) is { Length: > 0 } name ? name : path, null, path, path, FileKind.Directory, EventQuality.Unknown, DiffPrimaryOperation.Unknown, DiffSemanticState.Unknown, Array.Empty<DiffPrimaryOperation>(), null, true, false, false, false, "Virtual folder", 0, Array.Empty<string>(), Array.Empty<ReplayTimelinePoint>(), Array.Empty<EventId>());
}

/// <summary>Headless Tree renderer with expandable rows and no eager descendant control creation.</summary>
public sealed class DiffTreeView
{
    private readonly DiffViewModel model;

    /// <summary>Initializes a Tree renderer over a Diff View model.</summary>
    public DiffTreeView(DiffViewModel model) => this.model = model ?? throw new ArgumentNullException(nameof(model));

    /// <summary>Returns only rows currently visible after expansion.</summary>
    public IReadOnlyList<DiffTreeRow> GetVisibleRows()
    {
        var result = new List<DiffTreeRow>();
        foreach (var root in model.RootNodes) AddVisible(root, result);
        return result;
    }

    private static void AddVisible(DiffTreeNode node, ICollection<DiffTreeRow> rows)
    {
        rows.Add(new(node, node.Depth, node.HasUnloadedChildren || node.Children.Count > 0, node.IsExpanded, node.Visuals));
        if (node.IsExpanded)
        {
            foreach (var child in node.Children) AddVisible(child, rows);
        }
    }
}
