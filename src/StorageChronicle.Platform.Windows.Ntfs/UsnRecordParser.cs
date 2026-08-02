using System.Buffers.Binary;
using System.Text;

namespace StorageChronicle.Platform.Windows.Ntfs;

/// <summary>Parses documented USN_RECORD_V2 buffers without reading raw MFT sectors.</summary>
public sealed class UsnRecordParser
{
    private const int RecordHeaderSize = 60;

    /// <summary>Parses records that begin at offset zero and rejects malformed bounds.</summary>
    public IReadOnlyList<UsnRecord> Parse(ReadOnlySpan<byte> buffer)
    {
        var records = new List<UsnRecord>();
        ParseRecords(buffer, records);
        return records;
    }

    /// <summary>Parses an FSCTL_READ_USN_JOURNAL output buffer and returns its continuation USN.</summary>
    public IReadOnlyList<UsnRecord> ParseReadBuffer(ReadOnlySpan<byte> buffer, out long nextUsn)
    {
        if (buffer.Length < sizeof(long))
        {
            throw new InvalidDataException("The USN read buffer does not contain a continuation USN.");
        }

        nextUsn = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        var records = new List<UsnRecord>();
        ParseRecords(buffer[sizeof(long)..], records);
        return records;
    }

    /// <summary>Parses an FSCTL_ENUM_USN_DATA output buffer and returns its continuation file reference.</summary>
    public IReadOnlyList<UsnRecord> ParseEnumBuffer(ReadOnlySpan<byte> buffer, out ulong nextFileReferenceNumber)
    {
        if (buffer.Length < sizeof(ulong))
        {
            throw new InvalidDataException("The USN enumeration buffer does not contain a continuation file reference.");
        }

        nextFileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        var records = new List<UsnRecord>();
        ParseRecords(buffer[sizeof(ulong)..], records);
        return records;
    }

    private static void ParseRecords(ReadOnlySpan<byte> buffer, ICollection<UsnRecord> records)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            if (buffer.Length - offset < sizeof(int))
            {
                if (buffer[offset..].IndexOfAnyExcept((byte)0) >= 0)
                {
                    throw new InvalidDataException("USN record padding is malformed.");
                }

                return;
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
            if (length == 0)
            {
                return;
            }

            if (length < RecordHeaderSize || length > buffer.Length - offset)
            {
                throw new InvalidDataException("USN record length is malformed.");
            }

            records.Add(ParseRecord(buffer.Slice(offset, length)));
            offset += length;
        }
    }

    private static UsnRecord ParseRecord(ReadOnlySpan<byte> record)
    {
        var major = BinaryPrimitives.ReadInt16LittleEndian(record.Slice(4, sizeof(short)));
        var minor = BinaryPrimitives.ReadInt16LittleEndian(record.Slice(6, sizeof(short)));
        if (major != 2 || minor < 0)
        {
            throw new InvalidDataException("Unsupported USN record version.");
        }

        var fileId = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(8, sizeof(long)));
        var parentId = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(16, sizeof(long)));
        var usn = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(24, sizeof(long)));
        var reason = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(40, sizeof(int)));
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(52, sizeof(uint)));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(56, sizeof(ushort)));
        var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(58, sizeof(ushort)));
        if (nameLength % 2 != 0 || nameOffset < RecordHeaderSize || nameOffset > record.Length - nameLength)
        {
            throw new InvalidDataException("USN name bounds are malformed.");
        }

        var name = Encoding.Unicode.GetString(record.Slice(nameOffset, nameLength));
        return new UsnRecord(fileId, parentId, usn, reason, name) { FileAttributes = attributes };
    }
}

/// <summary>USN reason flags used by the documented V2 record.</summary>
public static class UsnReason
{
    /// <summary>File or directory was created.</summary>
    public const int FileCreate = 0x00000100;
    /// <summary>Data was written.</summary>
    public const int DataOverwrite = 0x00000001;
    /// <summary>Data was extended.</summary>
    public const int DataExtend = 0x00000002;
    /// <summary>Data was truncated.</summary>
    public const int DataTruncation = 0x00000004;
    /// <summary>Basic metadata changed.</summary>
    public const int BasicInfoChange = 0x00008000;
    /// <summary>Security metadata changed.</summary>
    public const int SecurityChange = 0x00000800;
    /// <summary>Old name in a rename pair.</summary>
    public const int RenameOldName = 0x00001000;
    /// <summary>New name in a rename pair.</summary>
    public const int RenameNewName = 0x00002000;
    /// <summary>File or directory was deleted.</summary>
    public const int FileDelete = 0x00000200;
    /// <summary>File or directory was closed.</summary>
    public const int Close = unchecked((int)0x80000000u);
}

/// <summary>Minimal documented USN facts used by normalization and reconciliation.</summary>
public sealed record UsnRecord(long FileId, long ParentFileId, long Usn, int Reason, string Name)
{
    /// <summary>Gets the standard file attributes reported by the USN record.</summary>
    public uint FileAttributes { get; init; }

    /// <summary>Gets the file-reference sequence number that prevents stale File ID reuse.</summary>
    public ushort FileReferenceSequence => unchecked((ushort)((ulong)FileId >> 48));

    /// <summary>Gets whether the record describes a directory.</summary>
    public bool IsDirectory => (FileAttributes & 0x10) != 0;
}

/// <summary>Pairs old and new names only when the complete file reference, including sequence, matches.</summary>
public sealed class UsnRenamePairer
{
    private readonly Dictionary<long, UsnRecord> pendingOldNames = new();

    /// <summary>Consumes a record and returns a pair only when both rename records are observed.</summary>
    public UsnRenamePair? Add(UsnRecord record)
    {
        if ((record.Reason & UsnReason.RenameOldName) != 0)
        {
            pendingOldNames[record.FileId] = record;
            return null;
        }

        if ((record.Reason & UsnReason.RenameNewName) == 0 || !pendingOldNames.Remove(record.FileId, out var oldName))
        {
            return null;
        }

        return new UsnRenamePair(record.FileId, oldName.Name, record.Name, oldName.Usn, record.Usn, record.FileReferenceSequence);
    }

    /// <summary>Drains old-name records that never received a matching new-name record.</summary>
    public IReadOnlyList<UsnRecord> DrainUnpaired()
    {
        var values = pendingOldNames.Values.ToArray();
        pendingOldNames.Clear();
        return values;
    }
}

/// <summary>Represents one confirmed rename old-name/new-name pair.</summary>
public sealed record UsnRenamePair(long FileId, string OldName, string NewName, long OldUsn, long NewUsn, ushort FileReferenceSequence);

/// <summary>Represents the persisted journal identity and next read position.</summary>
public sealed record UsnJournalState(ulong JournalId, long NextUsn);
