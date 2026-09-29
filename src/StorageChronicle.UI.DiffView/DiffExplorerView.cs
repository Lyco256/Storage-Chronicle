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
}

