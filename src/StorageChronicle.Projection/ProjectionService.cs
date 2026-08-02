using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Projection;

/// <summary>OS-independent projection service used by Agent and UI adapters.</summary>
public sealed class ProjectionService : IProjectionService
{
    private readonly ProjectionDocument document;
    private readonly ProjectionSettings settings;
    private readonly EventStackProjector eventStack;
    private readonly DiffProjector diff;

    /// <summary>Creates a regenerable service from immutable event material.</summary>
    public ProjectionService(ProjectionDocument document, ProjectionSettings? settings = null)
    {
        this.document = document ?? throw new ArgumentNullException(nameof(document));
        this.settings = settings ?? new ProjectionSettings();
        eventStack = new EventStackProjector();
        diff = new DiffProjector();
    }

    /// <summary>Gets a page using the shared contract's default query surface.</summary>
    public ValueTask<ProjectionPage<EventStackRow>> GetEventStackAsync(EventStackMode mode, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(eventStack.GetPage(document, new EventStackQuery(mode, page, pageSize), settings));
    }

    /// <summary>Gets a filtered and sorted Event Stack page.</summary>
    public ValueTask<ProjectionPage<EventStackRow>> GetEventStackAsync(EventStackQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(eventStack.GetPage(document, query, settings));
    }

    /// <summary>Gets an expandable page whose descendants never cross the page boundary.</summary>
    public ValueTask<ProjectionPage<EventStackNode>> GetEventStackTreeAsync(EventStackQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(eventStack.GetTreePage(document, query, settings));
    }

    /// <summary>Gets a shared Tree/Explorer diff projection with detailed semantic state.</summary>
    public ValueTask<DiffProjection> GetDiffProjectionAsync(DateTimeOffset? fromUtc, DateTimeOffset toUtc, DiffMode mode, ProjectionFilter? filter = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(diff.Project(document, fromUtc, toUtc, mode, filter ?? ProjectionFilter.Empty));
    }

    /// <summary>Gets domain diff rows through the stable shared contract.</summary>
    public ValueTask<IReadOnlyList<DiffEntry>> GetDiffAsync(DateTimeOffset? fromUtc, DateTimeOffset toUtc, DiffMode mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(diff.Project(document, fromUtc, toUtc, mode, ProjectionFilter.Empty).DomainEntries);
    }
}
