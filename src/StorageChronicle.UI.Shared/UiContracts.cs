using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.UI.Shared;

/// <summary>Identifies a feature view registered with the desktop shell.</summary>
public interface IFeatureView
{
    /// <summary>Feature identifier shown in the top navigation.</summary>
    string Id { get; }
    /// <summary>Display title shown in the top navigation.</summary>
    string Title { get; }
}

/// <summary>Resolves semantic operation meanings to stable icon names.</summary>
public interface IOperationIconResolver
{
    /// <summary>Returns the icon key for a semantic operation.</summary>
    string Resolve(OperationIconMeaning meaning);
}

/// <summary>Defines the common virtualized page contract used by both screens.</summary>
public interface IVirtualizedPageSource<T>
{
    /// <summary>Loads only the requested page.</summary>
    ValueTask<ProjectionPage<T>> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken = default);
}

/// <summary>Represents the fixed wording and choices for a reconciliation confirmation.</summary>
public sealed record ReconciliationPrompt(string Volume, string FileSystem, string Reason, DateTimeOffset? GapStart, DateTimeOffset DiscoveredAt);
