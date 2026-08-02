using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Contracts;

/// <summary>Writes immutable source and canonical events without update or delete operations.</summary>
public interface IEventStore
{
    /// <summary>Appends a source event.</summary>
    ValueTask AppendSourceAsync(SourceEvent value, CancellationToken cancellationToken = default);
    /// <summary>Appends a canonical event.</summary>
    ValueTask AppendCanonicalAsync(CanonicalEvent value, CancellationToken cancellationToken = default);
    /// <summary>Reads durable events in source order.</summary>
    IAsyncEnumerable<CanonicalEvent> ReadCanonicalAsync(CancellationToken cancellationToken = default);
}

/// <summary>Applies canonical events and returns immutable state snapshots.</summary>
public interface IStateStore
{
    /// <summary>Applies one event idempotently.</summary>
    ValueTask ApplyAsync(CanonicalEvent value, CancellationToken cancellationToken = default);
    /// <summary>Returns state at the requested instant.</summary>
    ValueTask<FileStateSnapshot> GetSnapshotAsync(DateTimeOffset atUtc, CancellationToken cancellationToken = default);
}

/// <summary>Converts source facts into durable canonical events.</summary>
public interface IEventNormalizer
{
    /// <summary>Normalizes one source event deterministically.</summary>
    CanonicalEvent? Normalize(SourceEvent value);
}

/// <summary>Collects source facts from a platform capability.</summary>
public interface ISourceEventCollector
{
    /// <summary>Provides a bounded asynchronous stream of source events.</summary>
    IAsyncEnumerable<SourceEvent> CollectAsync(CancellationToken cancellationToken = default);
}

/// <summary>Builds event-stack and diff projections without changing durable history.</summary>
public interface IProjectionService
{
    /// <summary>Gets a page of event-stack rows.</summary>
    ValueTask<ProjectionPage<EventStackRow>> GetEventStackAsync(EventStackMode mode, int page, int pageSize, CancellationToken cancellationToken = default);
    /// <summary>Gets a diff between two instants.</summary>
    ValueTask<IReadOnlyList<DiffEntry>> GetDiffAsync(DateTimeOffset? fromUtc, DateTimeOffset toUtc, DiffMode mode, CancellationToken cancellationToken = default);
}

/// <summary>Reads platform volume capabilities.</summary>
public interface IVolumeEnumerator
{
    /// <summary>Enumerates readable local volumes.</summary>
    ValueTask<IReadOnlyList<VolumeDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default);
}

/// <summary>Describes a local volume without requiring a drive letter.</summary>
public sealed record VolumeDescriptor(VolumeId Id, string FileSystem, IReadOnlyList<string> MountPoints, bool IsReadOnly, bool IsExternal, bool IsSystem, bool SupportsUsn, bool IsDirectoryReadable);

/// <summary>Provides capability detection for Windows-version-specific APIs.</summary>
public interface IPlatformCapabilities
{
    /// <summary>Returns whether an API capability is available.</summary>
    bool IsSupported(string capability);
}

/// <summary>Frames versioned local IPC messages.</summary>
public interface ILocalMessageCodec
{
    /// <summary>Encodes a payload with its protocol version.</summary>
    byte[] Encode<T>(T payload, int major, int minor);
    /// <summary>Decodes a payload and rejects unsupported protocol majors.</summary>
    T Decode<T>(ReadOnlySpan<byte> frame, int supportedMajor);
}
