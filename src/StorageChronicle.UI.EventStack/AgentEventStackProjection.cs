using StorageChronicle.Contracts.Runtime;
using StorageChronicle.UI.Shared;

namespace StorageChronicle.UI.EventStack;

/// <summary>Adapts the Agent pipe page contract to the rich Event Stack query contract.</summary>
public sealed class AgentEventStackProjection : IEventStackProjection
{
    private readonly AgentPipeProjectionClient client;

    /// <summary>Initializes an Agent-backed Event Stack source.</summary>
    public AgentEventStackProjection(AgentPipeProjectionClient? client = null) => this.client = client ?? new AgentPipeProjectionClient();

    /// <inheritdoc />
    public async ValueTask<EventStackPage> GetPageAsync(EventStackQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var response = await client.GetPageAsync(new ProjectionPageRequest(query.Mode, query.Page, query.PageSize,
            string.IsNullOrWhiteSpace(query.Filter.SearchText) ? null : query.Filter.SearchText,
            query.SortDirection == EventStackSortDirection.Descending,
            query.Filter.Quality, query.Filter.Origin, query.Filter.Operation), cancellationToken).ConfigureAwait(false);
        var nodes = response.Nodes;
        if (nodes is null || nodes.Count == 0)
        {
            return new EventStackPage(response.Items.Select(row => new EventStackItem(row, Array.Empty<StorageChronicle.Domain.Contracts.EventStackRow>(), null, null, query.Mode == StorageChronicle.Domain.Contracts.EventStackMode.Grouped, query.ExpandedGroups.Contains(row.Id))).ToArray(), response.Page, response.PageSize, response.TotalCount, response.HasMore);
        }

        return new EventStackPage(nodes.Select(node => ToItem(node, query)).ToArray(), response.Page, response.PageSize, response.TotalCount, response.HasMore);
    }

    /// <inheritdoc />
    public ValueTask<EventStackDetails?> GetDetailsAsync(StorageChronicle.Domain.Contracts.EventId eventId, CancellationToken cancellationToken = default)
        => GetDetailsCoreAsync(eventId, cancellationToken);

    private async ValueTask<EventStackDetails?> GetDetailsCoreAsync(StorageChronicle.Domain.Contracts.EventId eventId, CancellationToken cancellationToken)
    {
        var value = await client.GetDetailsAsync(eventId, cancellationToken).ConfigureAwait(false);
        return value is null ? null : new EventStackDetails(value.Row, value.SourceOrigin, value.Quality, value.RecordedUtc, value.LocalOffset, value.SourceSequence, value.MountSequence,
            value.ProcessName, value.ParentProcess, value.ChildProcesses, value.IsReconciliation, value.IsUnverifiedGap, value.IsExistenceOnly);
    }

    private static EventStackItem ToItem(EventStackNodeSnapshot node, EventStackQuery query)
    {
        var children = node.Children.Select(value => value.Row).ToArray();
        var nestedChildren = node.Children.Select(value => ToItem(value, query)).ToArray();
        var isGroup = query.Mode == StorageChronicle.Domain.Contracts.EventStackMode.Grouped &&
            (string.Equals(node.Kind, "Activity", StringComparison.Ordinal) || string.Equals(node.Kind, "Gap", StringComparison.Ordinal));
        return new EventStackItem(node.Row, children, null,
            string.IsNullOrWhiteSpace(node.ProcessDisplayName) ? null : node.ProcessDisplayName,
            isGroup, query.ExpandedGroups.Contains(node.Row.Id), nestedChildren);
    }
}
