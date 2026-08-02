using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Projection;

internal static class ProjectionOperationRules
{
    private static readonly CanonicalOperation[] Priority =
    [
        CanonicalOperation.Delete, CanonicalOperation.Recycle, CanonicalOperation.Restore,
        CanonicalOperation.Move, CanonicalOperation.Create, CanonicalOperation.DirectoryCreate,
        CanonicalOperation.Rename, CanonicalOperation.DataWrite, CanonicalOperation.Extend,
        CanonicalOperation.Truncate, CanonicalOperation.ShareChanged, CanonicalOperation.CloudStateChanged,
        CanonicalOperation.MetadataChanged, CanonicalOperation.SecurityMetadataChanged,
        CanonicalOperation.ReconciliationDiscovered, CanonicalOperation.UnverifiedGap
    ];

    public static CanonicalOperation Primary(IEnumerable<CanonicalOperation> operations)
    {
        var values = operations.ToArray();
        return Priority.FirstOrDefault(candidate => values.Contains(candidate));
    }

    public static DiffPrimaryOperation ToDiffOperation(CanonicalOperation operation, CanonicalEvent value)
    {
        return operation switch
        {
            CanonicalOperation.Delete => DiffPrimaryOperation.Delete,
            CanonicalOperation.Recycle => DiffPrimaryOperation.Recycle,
            CanonicalOperation.Restore => DiffPrimaryOperation.Restore,
            CanonicalOperation.Move when value.Properties.ContainsKey(ProjectionPropertyNames.OldPath) => DiffPrimaryOperation.MoveTo,
            CanonicalOperation.Move => DiffPrimaryOperation.MoveTo,
            CanonicalOperation.Create or CanonicalOperation.DirectoryCreate => IsCopy(value) ? DiffPrimaryOperation.Copy : DiffPrimaryOperation.Create,
            CanonicalOperation.Rename => DiffPrimaryOperation.Rename,
            CanonicalOperation.DataWrite => DiffPrimaryOperation.DataWrite,
            CanonicalOperation.Extend => DiffPrimaryOperation.Resize,
            CanonicalOperation.Truncate => DiffPrimaryOperation.Truncate,
            CanonicalOperation.ShareChanged => DiffPrimaryOperation.Share,
            CanonicalOperation.CloudStateChanged => DiffPrimaryOperation.CloudState,
            CanonicalOperation.UnverifiedGap or CanonicalOperation.ReconciliationDiscovered => DiffPrimaryOperation.Unknown,
            _ => DiffPrimaryOperation.MetadataChange
        };
    }

    public static DiffSemanticState ToSemanticState(DiffPrimaryOperation operation) => operation switch
    {
        DiffPrimaryOperation.Delete or DiffPrimaryOperation.Recycle => DiffSemanticState.Removed,
        DiffPrimaryOperation.Create or DiffPrimaryOperation.Copy => DiffSemanticState.Added,
        DiffPrimaryOperation.MoveFrom or DiffPrimaryOperation.MoveTo => DiffSemanticState.Moved,
        DiffPrimaryOperation.Rename => DiffSemanticState.Renamed,
        DiffPrimaryOperation.DataWrite or DiffPrimaryOperation.Resize or DiffPrimaryOperation.Truncate => DiffSemanticState.Edited,
        DiffPrimaryOperation.Share => DiffSemanticState.Shared,
        DiffPrimaryOperation.CloudState => DiffSemanticState.Cloud,
        DiffPrimaryOperation.Unknown => DiffSemanticState.Unknown,
        _ => DiffSemanticState.Metadata
    };

    public static bool IsRead(CanonicalEvent value) => value.IsReadOnlyObservation;

    public static bool IsCopy(CanonicalEvent value) => value.Properties.TryGetValue("copy", out var copy) &&
                                                       string.Equals(copy, "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsDeleted(CanonicalOperation operation) => operation is CanonicalOperation.Delete or CanonicalOperation.Recycle;
}
