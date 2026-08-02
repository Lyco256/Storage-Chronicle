using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Application;

/// <summary>Receives already-durable canonical events for bounded secondary projections.</summary>
public interface ICanonicalEventSink
{
    /// <summary>Accepts one canonical event without changing the primary event ordering.</summary>
    ValueTask OnCanonicalAsync(CanonicalEvent value, CancellationToken cancellationToken = default);

    /// <summary>Flushes pending secondary records before a supervised restart.</summary>
    ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <summary>Stops the sink after all pending records have been flushed.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
