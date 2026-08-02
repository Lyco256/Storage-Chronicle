using System.Runtime.CompilerServices;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.Ntfs;

/// <summary>Provides standard metadata only for the directory entries selected by reconciliation.</summary>
public interface IDirectoryMetadataProvider
{
    /// <summary>Reads one bounded directory batch without reading file contents.</summary>
    ValueTask<IReadOnlyList<FileMetadata>> ReadDirectoryBatchAsync(FileId parentFileId, IReadOnlyList<MftEntry> entries, CancellationToken cancellationToken = default);
}

/// <summary>Streams MFT candidates to a metadata provider in bounded directory batches.</summary>
public sealed class DirectoryMetadataBatcher
{
    private readonly IDirectoryMetadataProvider provider;
    private readonly int batchSize;

    /// <summary>Initializes a batcher for changed candidates only.</summary>
    public DirectoryMetadataBatcher(IDirectoryMetadataProvider provider, int batchSize = 256)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        this.batchSize = batchSize;
    }

    /// <summary>Streams standard metadata and never buffers the complete MFT.</summary>
    public async IAsyncEnumerable<FileMetadata> ReadAsync(IAsyncEnumerable<MftEntry> candidates, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var batch = new List<MftEntry>(batchSize);
        await foreach (var candidate in candidates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            batch.Add(candidate);
            if (batch.Count < batchSize) continue;
            await foreach (var metadata in ReadBatchAsync(batch, cancellationToken).ConfigureAwait(false)) yield return metadata;
            batch.Clear();
        }

        if (batch.Count > 0)
        {
            await foreach (var metadata in ReadBatchAsync(batch, cancellationToken).ConfigureAwait(false)) yield return metadata;
        }
    }

    private async IAsyncEnumerable<FileMetadata> ReadBatchAsync(List<MftEntry> batch, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var group in batch.GroupBy(entry => entry.ParentFileReferenceNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = FileId.Create(group.Key.ToString("X16", System.Globalization.CultureInfo.InvariantCulture));
            var metadata = await provider.ReadDirectoryBatchAsync(parent, group.ToArray(), cancellationToken).ConfigureAwait(false);
            foreach (var item in metadata) yield return item;
        }
    }
}
