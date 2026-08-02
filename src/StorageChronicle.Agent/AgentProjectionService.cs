using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Projection;
using StorageChronicle.Storage;

namespace StorageChronicle.Agent;

/// <summary>Rebuilds UI projections from the immutable Agent log on demand.</summary>
public sealed class AgentProjectionService : IProjectionService
{
    private readonly AppendOnlyStorageEngine store;
    private readonly ProjectionSettings settings;

    /// <summary>Initializes a projection adapter over one durable store.</summary>
    public AgentProjectionService(AppendOnlyStorageEngine store, ProjectionSettings? settings = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.settings = settings ?? new ProjectionSettings();
    }

    /// <inheritdoc />
    public async ValueTask<ProjectionPage<EventStackRow>> GetEventStackAsync(EventStackMode mode, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var offset = checked((page - 1) * pageSize);
        if (mode == EventStackMode.Source)
        {
            var sources = await store.ReadSourcePageAsync(offset, pageSize, cancellationToken: cancellationToken).ConfigureAwait(false);
            var total = await store.CountEventsAsync(canonical: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            var pageResult = await new ProjectionService(new ProjectionDocument(Array.Empty<CanonicalEvent>(), sources), settings).GetEventStackAsync(mode, 1, pageSize, cancellationToken).ConfigureAwait(false);
            return new ProjectionPage<EventStackRow>(pageResult.Items, page, pageSize, total, offset + pageResult.Items.Count < total);
        }

        if (mode == EventStackMode.Normalized)
        {
            var canonical = await store.ReadCanonicalPageAsync(offset, pageSize, cancellationToken: cancellationToken).ConfigureAwait(false);
            var total = await store.CountEventsAsync(canonical: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            var pageResult = await new ProjectionService(new ProjectionDocument(canonical, Array.Empty<SourceEvent>()), settings).GetEventStackAsync(mode, 1, pageSize, cancellationToken).ConfigureAwait(false);
            return new ProjectionPage<EventStackRow>(pageResult.Items, page, pageSize, total, offset + pageResult.Items.Count < total);
        }

        // Group boundaries are derived from adjacent canonical events. Keep this path
        // regeneration-based until the durable grouped index is built, so no group is
        // silently split at an IPC page boundary.
        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        return await new ProjectionService(document, settings).GetEventStackAsync(mode, page, pageSize, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<DiffEntry>> GetDiffAsync(DateTimeOffset? fromUtc, DateTimeOffset toUtc, DiffMode mode, CancellationToken cancellationToken = default)
    {
        if (toUtc == default) throw new ArgumentException("A diff end time is required.", nameof(toUtc));
        if (fromUtc is { } from && from > toUtc) throw new ArgumentException("Diff start must not be after its end.", nameof(fromUtc));
        var canonical = new List<CanonicalEvent>();
        var offset = 0;
        const int batchSize = 2048;
        while (true)
        {
            var batch = await store.ReadCanonicalPageAsync(offset, batchSize, fromUtc, toUtc, cancellationToken).ConfigureAwait(false);
            canonical.AddRange(batch);
            if (batch.Count < batchSize) break;
            offset = checked(offset + batch.Count);
        }

        // Diff projections do not need source facts; keeping the query range bounded
        // avoids loading unrelated source history into the Agent process.
        var document = new ProjectionDocument(canonical, Array.Empty<SourceEvent>());
        return await new ProjectionService(document, settings).GetDiffAsync(fromUtc, toUtc, mode, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ProjectionDocument> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        var canonical = new List<CanonicalEvent>();
        await foreach (var value in store.ReadCanonicalAsync(cancellationToken).ConfigureAwait(false)) canonical.Add(value);
        var source = new List<SourceEvent>();
        await foreach (var value in store.ReadSourceAsync(cancellationToken).ConfigureAwait(false)) source.Add(value);
        return new ProjectionDocument(canonical, source);
    }
}
