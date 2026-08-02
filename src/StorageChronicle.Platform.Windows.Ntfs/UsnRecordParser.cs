using System.Buffers.Binary;
using System.Text;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.Ntfs;

/// <summary>Parses documented USN_RECORD_V2 buffers without reading raw MFT sectors.</summary>
public sealed class UsnRecordParser
{
    private const int HeaderSize = 60;
    /// <summary>Parses a buffer and skips zero padding between records.</summary>
    public IReadOnlyList<UsnRecord> Parse(ReadOnlySpan<byte> buffer)
    {
        var records = new List<UsnRecord>();
        var offset = 0;
        while (offset + sizeof(int) <= buffer.Length)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
            if (length == 0) break;
            if (length < HeaderSize || offset + length > buffer.Length) throw new InvalidDataException("USN record length is malformed.");
            var record = buffer.Slice(offset, length);
            var major = BinaryPrimitives.ReadInt16LittleEndian(record.Slice(4, 2));
            if (major != 2) throw new InvalidDataException("Unsupported USN record version.");
            var fileId = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(8, 8));
            var parentId = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(16, 8));
            var usn = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(24, 8));
            var reason = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(40, 4));
            var nameLength = BinaryPrimitives.ReadInt16LittleEndian(record.Slice(56, 2));
            var nameOffset = BinaryPrimitives.ReadInt16LittleEndian(record.Slice(58, 2));
            if (nameLength < 0 || nameLength % 2 != 0 || nameOffset < HeaderSize || nameOffset + nameLength > length) throw new InvalidDataException("USN name bounds are malformed.");
            var name = Encoding.Unicode.GetString(record.Slice(nameOffset, nameLength));
            records.Add(new UsnRecord(fileId, parentId, usn, reason, name));
            offset += length;
        }
        return records;
    }
}

/// <summary>Minimal documented USN record facts used by normalization.</summary>
public sealed record UsnRecord(long FileId, long ParentFileId, long Usn, int Reason, string Name);

/// <summary>Represents the journal continuity state persisted by the agent.</summary>
public sealed record UsnJournalState(Guid JournalId, long NextUsn);

/// <summary>Detects Windows API capabilities without assuming Windows 11.</summary>
public sealed class Windows10CapabilityDetector : IPlatformCapabilities
{
    /// <inheritdoc />
    public bool IsSupported(string capability) => capability switch
    {
        "FsctlQueryUsnJournal" or "FsctlReadUsnJournal" or "FsctlEnumUsnData" => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041),
        "CloudPlaceholder" => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041),
        _ => false
    };
}
