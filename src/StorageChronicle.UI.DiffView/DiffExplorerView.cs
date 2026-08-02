namespace StorageChronicle.UI.DiffView;

/// <summary>Headless virtualized Explorer renderer for the eight required presentation modes.</summary>
public sealed class DiffExplorerView
{
    private readonly DiffViewModel model;

    /// <summary>Initializes an Explorer renderer over a Diff View model.</summary>
    public DiffExplorerView(DiffViewModel model) => this.model = model ?? throw new ArgumentNullException(nameof(model));

    /// <summary>Gets the eight stable Explorer presentation modes.</summary>
    public static IReadOnlyList<ExplorerViewMode> SupportedModes { get; } = Enum.GetValues<ExplorerViewMode>();

    /// <summary>Returns one bounded, one-based Explorer page.</summary>
    public IReadOnlyList<DiffExplorerRow> GetPage(int page, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var skip = checked((page - 1) * pageSize);
        return model.ExplorerRows.Skip(skip).Take(pageSize).ToArray();
    }
}

