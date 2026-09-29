using StorageChronicle.Projection;

namespace StorageChronicle.UI.DiffView;

/// <summary>Headless virtualized Explorer renderer for the eight required presentation modes.</summary>
public sealed class DiffExplorerView
{
    /// <summary>Minimum supported icon zoom percentage.</summary>
    public const int MinimumZoomPercent = 50;
    /// <summary>Maximum supported icon zoom percentage.</summary>
    public const int MaximumZoomPercent = 300;
    private readonly DiffViewModel model;

    /// <summary>Initializes an Explorer renderer over a Diff View model.</summary>
    public DiffExplorerView(DiffViewModel model) => this.model = model ?? throw new ArgumentNullException(nameof(model));

    /// <summary>Gets the eight stable Explorer presentation modes.</summary>
    public static IReadOnlyList<ExplorerViewMode> SupportedModes { get; } = Enum.GetValues<ExplorerViewMode>();

    /// <summary>Gets the current continuous icon zoom percentage, shared by every presentation mode.</summary>
    public int ZoomPercent { get; private set; } = 100;

    /// <summary>Sets icon zoom, rejecting values outside the supported 50–300 percent range.</summary>
    public void SetZoomPercent(int percent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, MinimumZoomPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, MaximumZoomPercent);
        ZoomPercent = percent;
    }

    /// <summary>Adjusts continuous zoom and clamps the result to the supported range.</summary>
    public void AdjustZoom(int deltaPercent) => ZoomPercent = Math.Clamp(checked(ZoomPercent + deltaPercent), MinimumZoomPercent, MaximumZoomPercent);

    /// <summary>Returns one bounded, one-based Explorer page.</summary>
    public IReadOnlyList<DiffExplorerRow> GetPage(int page, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var skip = checked((page - 1) * pageSize);
        return model.ExplorerRows.Skip(skip).Take(pageSize).ToArray();
    }

    /// <summary>Returns immediate projected children of a folder, synthesizing only path-grouping folders.</summary>
    public IReadOnlyList<DiffExplorerRow> GetChildrenPage(string folderPath, int page, int pageSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var parent = StorageChronicle.Projection.ProjectionPathResolver.Normalize(folderPath) ?? folderPath;
        var prefix = parent.EndsWith('/') ? parent : parent + "/";
        var children = new Dictionary<string, DiffExplorerRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in model.ExplorerRows)
        {
            var childPath = StorageChronicle.Projection.ProjectionPathResolver.Normalize(row.Projection.DisplayPath);
            if (childPath is null || !childPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var remainder = childPath[prefix.Length..];
            if (remainder.Length == 0) continue;
            var separator = remainder.IndexOf('/');
            if (separator < 0)
            {
                children[childPath] = row;
                continue;
            }

            var directoryPath = parent.TrimEnd('/') + "/" + remainder[..separator];
            children.TryAdd(directoryPath, VirtualDirectory(directoryPath, remainder[..separator], row));
        }

        var ordered = children.Values.OrderBy(row => row.Projection.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        var skip = checked((page - 1) * pageSize);
        return ordered.Skip(skip).Take(pageSize).ToArray();
    }

    private static DiffExplorerRow VirtualDirectory(string path, string name, DiffExplorerRow descendant)
    {
        var source = descendant.Projection;
        var projection = source with
        {
            FileId = null,
            DisplayName = name,
            OldPath = null,
            NewPath = path,
            DisplayPath = path,
            Kind = StorageChronicle.Domain.Contracts.FileKind.Directory,
            Quality = StorageChronicle.Domain.Contracts.EventQuality.Unknown,
            PrimaryOperation = DiffPrimaryOperation.Unknown,
            SemanticState = DiffSemanticState.Unknown,
            SubOperations = Array.Empty<DiffPrimaryOperation>(),
            RelatedOperationId = null,
            IsVirtual = true,
            IsDeleted = false,
            IsPeriodOnly = false,
            CanOpenInExplorer = false,
            OpenDisabledReason = "This grouping folder is virtual; select one of its projected descendants.",
            DescendantCount = 1,
            RenameHistory = Array.Empty<string>(),
            ReplayTimeline = Array.Empty<ReplayTimelinePoint>(),
            EventIds = Array.Empty<StorageChronicle.Domain.Contracts.EventId>()
        };
        return new("virtual-folder:" + path, projection, DiffVisuals.For(projection), false, projection.OpenDisabledReason);
    }
}
