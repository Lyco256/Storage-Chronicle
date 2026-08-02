using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Projection;

namespace StorageChronicle.UI.DiffView;

/// <summary>Describes the semantic gutter marker rendered beside a Tree or Explorer row.</summary>
public sealed record DiffGutterMarker(string Kind, string ColorToken, string IconKey, bool IsPrimary);

/// <summary>Contains the primary and secondary gutter markers for one diff row.</summary>
public sealed record DiffVisualSet(DiffGutterMarker Primary, IReadOnlyList<DiffGutterMarker> Secondary);

/// <summary>Provides stable color and icon tokens without making the projection UI-framework-specific.</summary>
public static class DiffVisuals
{
    /// <summary>Maps a projection's semantic state and operations to display tokens.</summary>
    public static DiffVisualSet For(FileDiffProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var primary = Marker(projection.SemanticState, projection.PrimaryOperation, isPrimary: true);
        var secondary = projection.SubOperations
            .Where(operation => operation != projection.PrimaryOperation)
            .Distinct()
            .Select(operation => Marker(ToState(operation), operation, isPrimary: false))
            .ToArray();
        return new(primary, secondary);
    }

    private static DiffGutterMarker Marker(DiffSemanticState state, DiffPrimaryOperation operation, bool isPrimary) => new(
        state.ToString(),
        state switch
        {
            DiffSemanticState.Added => "diff.added",
            DiffSemanticState.Removed => "diff.removed",
            DiffSemanticState.Edited => "diff.edited",
            DiffSemanticState.Moved => "diff.moved",
            DiffSemanticState.Renamed => "diff.renamed",
            DiffSemanticState.Shared => "diff.shared",
            DiffSemanticState.Cloud => "diff.cloud",
            DiffSemanticState.Reconciled => "diff.reconciled",
            _ => "diff.unknown"
        },
        operation switch
        {
            DiffPrimaryOperation.Delete => "delete",
            DiffPrimaryOperation.Recycle => "recycle",
            DiffPrimaryOperation.Restore => "restore",
            DiffPrimaryOperation.MoveFrom or DiffPrimaryOperation.MoveTo => "move",
            DiffPrimaryOperation.Copy => "copy",
            DiffPrimaryOperation.Create => "create",
            DiffPrimaryOperation.Rename => "rename",
            DiffPrimaryOperation.DataWrite or DiffPrimaryOperation.Resize or DiffPrimaryOperation.Truncate => "edit",
            DiffPrimaryOperation.Share => "share",
            DiffPrimaryOperation.CloudState => "cloud",
            _ => "metadata"
        },
        isPrimary);

    private static DiffSemanticState ToState(DiffPrimaryOperation operation) => operation switch
    {
        DiffPrimaryOperation.Delete or DiffPrimaryOperation.Recycle => DiffSemanticState.Removed,
        DiffPrimaryOperation.Create or DiffPrimaryOperation.Copy => DiffSemanticState.Added,
        DiffPrimaryOperation.MoveFrom or DiffPrimaryOperation.MoveTo => DiffSemanticState.Moved,
        DiffPrimaryOperation.Rename => DiffSemanticState.Renamed,
        DiffPrimaryOperation.DataWrite or DiffPrimaryOperation.Resize or DiffPrimaryOperation.Truncate => DiffSemanticState.Edited,
        DiffPrimaryOperation.Share => DiffSemanticState.Shared,
        DiffPrimaryOperation.CloudState => DiffSemanticState.Cloud,
        _ => DiffSemanticState.Metadata
    };
}

/// <summary>One row shared by the Explorer renderer and its virtualized page source.</summary>
public sealed record DiffExplorerRow(
    string RowId,
    FileDiffProjection Projection,
    DiffVisualSet Visuals,
    bool CanOpenInExplorer,
    string? OpenDisabledReason);

/// <summary>Represents a query sent to the common projection.</summary>
public sealed record DiffProjectionQuery(DateTimeOffset? FromUtc, DateTimeOffset ToUtc, DiffMode Mode, ProjectionFilter Filter);

/// <summary>Supplies the rich, OS-neutral diff projection used by both UI renderers.</summary>
public interface IDiffProjectionSource
{
    /// <summary>Loads a diff projection without reading the operating-system file system.</summary>
    ValueTask<DiffProjection> GetDiffProjectionAsync(DiffProjectionQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Adapts the concrete common Projection service to the UI source boundary.</summary>
public sealed class ProjectionServiceDiffSource : IDiffProjectionSource
{
    private readonly ProjectionService projection;

    /// <summary>Initializes the adapter over the common Projection implementation.</summary>
    public ProjectionServiceDiffSource(ProjectionService projection) => this.projection = projection ?? throw new ArgumentNullException(nameof(projection));

    /// <inheritdoc />
    public ValueTask<DiffProjection> GetDiffProjectionAsync(DiffProjectionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return projection.GetDiffProjectionAsync(query.FromUtc, query.ToUtc, query.Mode, query.Filter, cancellationToken);
    }
}

/// <summary>Adapts only the stable shared projection contract for tests or IPC-only hosts.</summary>
public sealed class ContractDiffProjectionSource : IDiffProjectionSource
{
    private readonly IProjectionService projection;

    /// <summary>Initializes the fallback adapter.</summary>
    public ContractDiffProjectionSource(IProjectionService projection) => this.projection = projection ?? throw new ArgumentNullException(nameof(projection));

    /// <inheritdoc />
    public async ValueTask<DiffProjection> GetDiffProjectionAsync(DiffProjectionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var entries = await projection.GetDiffAsync(query.FromUtc, query.ToUtc, query.Mode, cancellationToken).ConfigureAwait(false);
        var rows = entries.Select(Convert).ToArray();
        return new(query.Mode, query.ToUtc, rows.Where(row => !row.IsVirtual).ToArray(), rows.Where(row => row.IsVirtual).ToArray(), entries);
    }

    private static FileDiffProjection Convert(DiffEntry entry)
    {
        var operation = entry.Operation switch
        {
            CanonicalOperation.Delete => DiffPrimaryOperation.Delete,
            CanonicalOperation.Recycle => DiffPrimaryOperation.Recycle,
            CanonicalOperation.Restore => DiffPrimaryOperation.Restore,
            CanonicalOperation.Move => DiffPrimaryOperation.MoveTo,
            CanonicalOperation.Rename => DiffPrimaryOperation.Rename,
            CanonicalOperation.Create or CanonicalOperation.DirectoryCreate => DiffPrimaryOperation.Create,
            CanonicalOperation.DataWrite => DiffPrimaryOperation.DataWrite,
            CanonicalOperation.Extend => DiffPrimaryOperation.Resize,
            CanonicalOperation.Truncate => DiffPrimaryOperation.Truncate,
            CanonicalOperation.ShareChanged => DiffPrimaryOperation.Share,
            CanonicalOperation.CloudStateChanged => DiffPrimaryOperation.CloudState,
            _ => DiffPrimaryOperation.MetadataChange
        };
        var semantic = operation switch
        {
            DiffPrimaryOperation.Delete or DiffPrimaryOperation.Recycle => DiffSemanticState.Removed,
            DiffPrimaryOperation.Create => DiffSemanticState.Added,
            DiffPrimaryOperation.MoveTo => DiffSemanticState.Moved,
            DiffPrimaryOperation.Rename => DiffSemanticState.Renamed,
            DiffPrimaryOperation.DataWrite or DiffPrimaryOperation.Resize or DiffPrimaryOperation.Truncate => DiffSemanticState.Edited,
            DiffPrimaryOperation.Share => DiffSemanticState.Shared,
            DiffPrimaryOperation.CloudState => DiffSemanticState.Cloud,
            _ => DiffSemanticState.Metadata
        };
        var path = entry.NewPath ?? entry.OldPath;
        return new(entry.FileId, Path.GetFileName(path ?? "Unknown location"), entry.OldPath, entry.NewPath, path ?? "Unknown location", entry.Kind, entry.Quality, operation, semantic, Array.Empty<DiffPrimaryOperation>(), null, entry.IsVirtual, operation is DiffPrimaryOperation.Delete or DiffPrimaryOperation.Recycle, false, !entry.IsVirtual && entry.NewPath is not null, entry.IsVirtual ? "Virtual or unknown-location rows cannot be opened." : null, 0, Array.Empty<string>(), Array.Empty<ReplayTimelinePoint>(), Array.Empty<EventId>());
    }
}

/// <summary>Represents one attempt to open an existing item in the operating-system Explorer.</summary>
public sealed record ExplorerOpenResult(bool Succeeded, string? Error)
{
    /// <summary>Creates a rejected result without touching the operating system.</summary>
    public static ExplorerOpenResult Rejected(string error) => new(false, error);
}

/// <summary>Platform adapter invoked only after the UI has verified Explorer eligibility.</summary>
public interface IExplorerLauncher
{
    /// <summary>Opens an existing path in the host Explorer.</summary>
    ValueTask<ExplorerOpenResult> OpenAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Default adapter used by headless hosts; it never performs OS I/O.</summary>
public sealed class UnsupportedExplorerLauncher : IExplorerLauncher
{
    /// <inheritdoc />
    public ValueTask<ExplorerOpenResult> OpenAsync(string path, CancellationToken cancellationToken = default) => ValueTask.FromResult(ExplorerOpenResult.Rejected("Explorer integration is not available in this host."));
}
