using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Abstractions;

/// <summary>Reads a volume snapshot without traversing link targets.</summary>
public interface IVolumeSnapshotReader
{
    /// <summary>Reads an initial snapshot using bounded batches.</summary>
    IAsyncEnumerable<SourceEvent> ReadInitialSnapshotAsync(VolumeDescriptor volume, CancellationToken cancellationToken = default);
}

/// <summary>Supplies process/file-I/O correlations for bounded normalization buffers.</summary>
public interface IProcessEventSource
{
    /// <summary>Streams process observations.</summary>
    IAsyncEnumerable<SourceEvent> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Supplies clipboard intent from a user session agent.</summary>
public interface IClipboardEventSource
{
    /// <summary>Streams clipboard candidates without persisting clipboard contents.</summary>
    IAsyncEnumerable<SourceEvent> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Supplies local SMB share snapshots and changes.</summary>
public interface IShareStateSource
{
    /// <summary>Returns the current share state.</summary>
    ValueTask<IReadOnlyList<ShareDescriptor>> ReadSnapshotAsync(CancellationToken cancellationToken = default);
    /// <summary>Streams share changes.</summary>
    IAsyncEnumerable<SourceEvent> ReadChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Describes a local SMB share without remote-user history.</summary>
public sealed record ShareDescriptor(string Name, string LocalPath, string Type, string? Description, IReadOnlyList<string> Permissions);
