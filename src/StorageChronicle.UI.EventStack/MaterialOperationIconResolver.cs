using StorageChronicle.UI.Shared;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.UI.EventStack;

/// <summary>Maps semantic operation meanings to stable Material Icons identifiers.</summary>
public sealed class MaterialOperationIconResolver : IOperationIconResolver
{
    /// <inheritdoc />
    public string Resolve(OperationIconMeaning meaning) => meaning switch
    {
        OperationIconMeaning.Created => "AddCircleOutline",
        OperationIconMeaning.Edited => "Edit",
        OperationIconMeaning.Moved => "DriveFileMove",
        OperationIconMeaning.Renamed => "DriveFileRenameOutline",
        OperationIconMeaning.Deleted => "DeleteOutline",
        OperationIconMeaning.Recycled => "Recycling",
        OperationIconMeaning.Restored => "Restore",
        OperationIconMeaning.Shared => "Share",
        OperationIconMeaning.Reconciled => "FactCheck",
        _ => "HelpOutline"
    };
}
