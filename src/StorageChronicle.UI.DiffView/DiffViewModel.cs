using System.Collections.ObjectModel;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.UI.Shared;

namespace StorageChronicle.UI.DiffView;

/// <summary>Shared Tree/Explorer diff state; it never reads the OS file system.</summary>
public sealed class DiffViewModel : IFeatureView
{
    private readonly IProjectionService projection;
    /// <summary>Initializes a diff view over a platform-neutral projection.</summary>
    public DiffViewModel(IProjectionService projection) => this.projection = projection ?? throw new ArgumentNullException(nameof(projection));
    /// <inheritdoc />
    public string Id => "diff-view";
    /// <inheritdoc />
    public string Title => "Diff View";
    /// <summary>Current diff mode.</summary>
    public DiffMode Mode { get; private set; } = DiffMode.Live;
    /// <summary>Current explorer presentation.</summary>
    public ExplorerViewMode ViewMode { get; private set; } = ExplorerViewMode.Details;
    /// <summary>Whether live updates are paused in the UI.</summary>
    public bool IsPaused { get; private set; }
    /// <summary>Rows used by both Tree and Explorer renderers.</summary>
    public ObservableCollection<DiffEntry> Rows { get; } = [];
    /// <summary>Loads the same projection for either renderer.</summary>
    public async ValueTask RefreshAsync(DateTimeOffset? fromUtc, DateTimeOffset toUtc, DiffMode mode, CancellationToken cancellationToken = default)
    {
        Mode = mode;
        var rows = await projection.GetDiffAsync(fromUtc, toUtc, mode, cancellationToken).ConfigureAwait(false);
        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);
    }
    /// <summary>Sets a common Explorer display mode for every pane.</summary>
    public void SetViewMode(ExplorerViewMode mode) => ViewMode = mode;
    /// <summary>Pauses UI live projection while Agent recording continues.</summary>
    public void PauseLive() => IsPaused = true;
    /// <summary>Resumes UI live projection.</summary>
    public void ResumeLive() => IsPaused = false;
}

/// <summary>Supported Explorer layouts.</summary>
public enum ExplorerViewMode { ExtraLargeIcons, LargeIcons, MediumIcons, SmallIcons, List, Details, Tiles, Content }
