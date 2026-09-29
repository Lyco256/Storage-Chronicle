using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.Ntfs;

/// <summary>One lightweight entry returned by public FSCTL_ENUM_USN_DATA enumeration.</summary>
public sealed record MftEntry(ulong FileReferenceNumber, ulong ParentFileReferenceNumber, long Usn, string Name, uint FileAttributes)
{
    /// <summary>Gets the NTFS file-reference sequence used to reject stale ID reuse.</summary>
    public ushort FileReferenceSequence => unchecked((ushort)(FileReferenceNumber >> 48));
    /// <summary>Gets whether the entry is a directory.</summary>
    public bool IsDirectory => (FileAttributes & 0x10) != 0;
    /// <summary>Gets the platform-neutral file identifier.</summary>
    public FileId ToFileId() => FileId.Create(FileReferenceNumber.ToString("X16", System.Globalization.CultureInfo.InvariantCulture));
    /// <summary>Gets the platform-neutral parent identifier.</summary>
    public FileId ToParentFileId() => FileId.Create(ParentFileReferenceNumber.ToString("X16", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>Enumerates lightweight MFT facts through the public USN API.</summary>
public interface IMftEnumerator
{
    /// <summary>Streams entries in bounded batches and honors cancellation.</summary>
    IAsyncEnumerable<MftEntry> EnumerateAsync(CancellationToken cancellationToken = default);
}

/// <summary>Performs FSCTL_ENUM_USN_DATA enumeration without accessing raw $MFT sectors.</summary>
public sealed class WindowsMftEnumerator : IMftEnumerator
{
    private readonly INtfsApi api;
    private readonly string devicePath;
    private readonly int bufferSize;

    /// <summary>Initializes a public-API MFT enumerator.</summary>
    public WindowsMftEnumerator(INtfsApi api, string devicePath, int bufferSize = 64 * 1024)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        this.devicePath = string.IsNullOrWhiteSpace(devicePath) ? throw new ArgumentException("A volume device path is required.", nameof(devicePath)) : devicePath;
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 4096);
        this.bufferSize = bufferSize;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MftEntry> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var handle = api.OpenVolume(devicePath);
        var startFileReference = 0UL;
        var parser = new UsnRecordParser();
        var buffer = new byte[bufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = api.EnumerateUsnData(handle, new EnumUsnDataRequest(startFileReference, 0, long.MaxValue), buffer, out var bytesReturned);
            if (!result.Succeeded)
            {
                throw new NtfsAccessException(result.Status, result.Win32Error, $"FSCTL_ENUM_USN_DATA failed with {result.Status}.");
            }

            if (bytesReturned < sizeof(ulong)) yield break;
            var records = parser.ParseEnumBuffer(buffer.AsSpan(0, bytesReturned), out var nextFileReference);
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new MftEntry(unchecked((ulong)record.FileId), unchecked((ulong)record.ParentFileId), record.Usn, record.Name, record.FileAttributes);
            }

            if (records.Count == 0 || nextFileReference <= startFileReference) yield break;
            startFileReference = nextFileReference;
            await Task.Yield();
        }
    }
}

/// <summary>Describes one lightweight reconciliation difference; it is not a durable normal event.</summary>
public sealed record NtfsReconciliationCandidate(FileId FileId, FileId? StoredParentFileId, string? StoredName, long StoredUsn, MftEntry Current);

/// <summary>Compares saved IDs, parent/name relationships, and last USN without enumerating file contents.</summary>
public static class NtfsReconciliationComparer
{
    /// <summary>Returns candidates whose identity, parent, name, or USN differs.</summary>
    public static IReadOnlyList<NtfsReconciliationCandidate> Compare(IEnumerable<MftEntry> current, IReadOnlyDictionary<FileId, (FileId? Parent, string Name, long LastUsn)> saved)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(saved);
        var result = new List<NtfsReconciliationCandidate>();
        foreach (var entry in current)
        {
            var fileId = entry.ToFileId();
            if (!saved.TryGetValue(fileId, out var previous) || previous.Parent != entry.ToParentFileId() || !string.Equals(previous.Name, entry.Name, StringComparison.Ordinal) || previous.LastUsn != entry.Usn)
            {
                result.Add(new NtfsReconciliationCandidate(fileId, previous.Parent, previous.Name, previous.LastUsn, entry));
            }
        }

        return result;
    }
}
