using System.Buffers.Binary;
using System.Text;

namespace StorageChronicle.Platform.Windows.FileSystem.Monitoring;

/// <summary>Parses FILE_NOTIFY_INFORMATION records without interpreting file contents.</summary>
public static class ReadDirectoryChangesBufferParser
{
    /// <summary>Parses a native notification buffer and preserves a rename pair when present.</summary>
    public static DirectoryChangeParseResult Parse(ReadOnlySpan<byte> buffer, int bytesReturned, long firstSequence, DateTimeOffset receivedUtc)
    {
        if (bytesReturned < 0 || bytesReturned > buffer.Length)
        {
            return new DirectoryChangeParseResult(Array.Empty<DirectoryChangeNotification>(), true);
        }

        var notifications = new List<DirectoryChangeNotification>();
        var offset = 0;
        string? pendingRename = null;
        while (offset < bytesReturned)
        {
            if (bytesReturned - offset < 12)
            {
                return new DirectoryChangeParseResult(Array.Empty<DirectoryChangeNotification>(), true);
            }

            var nextOffset = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4));
            var action = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset + 4, 4));
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset + 8, 4));
            if (nextOffset < 0 || nameLength < 0 || nameLength % 2 != 0 || nameLength > bytesReturned - offset - 12)
            {
                return new DirectoryChangeParseResult(Array.Empty<DirectoryChangeNotification>(), true);
            }

            var name = Encoding.Unicode.GetString(buffer.Slice(offset + 12, nameLength));
            if (string.IsNullOrWhiteSpace(name) || name.Contains('\0'))
            {
                return new DirectoryChangeParseResult(Array.Empty<DirectoryChangeNotification>(), true);
            }

            var kind = action switch
            {
                1 => DirectoryChangeKind.Added,
                2 => DirectoryChangeKind.Removed,
                3 => DirectoryChangeKind.Modified,
                4 => DirectoryChangeKind.RenamedOldName,
                5 => DirectoryChangeKind.RenamedNewName,
                _ => (DirectoryChangeKind)(-1)
            };
            if ((int)kind < 0)
            {
                return new DirectoryChangeParseResult(Array.Empty<DirectoryChangeNotification>(), true);
            }

            var oldName = kind == DirectoryChangeKind.RenamedNewName ? pendingRename : null;
            if (kind == DirectoryChangeKind.RenamedOldName)
            {
                pendingRename = name;
            }
            else
            {
                notifications.Add(new DirectoryChangeNotification(firstSequence + notifications.Count, kind, name, oldName, receivedUtc));
                if (kind == DirectoryChangeKind.RenamedNewName)
                {
                    pendingRename = null;
                }
            }

            if (nextOffset == 0)
            {
                break;
            }

            if (nextOffset < 12 || nextOffset > bytesReturned - offset || nextOffset % 4 != 0)
            {
                return new DirectoryChangeParseResult(Array.Empty<DirectoryChangeNotification>(), true);
            }

            offset += nextOffset;
        }

        if (pendingRename is not null)
        {
            notifications.Add(new DirectoryChangeNotification(firstSequence + notifications.Count, DirectoryChangeKind.RenamedOldName, pendingRename, null, receivedUtc));
        }

        return new DirectoryChangeParseResult(notifications, false);
    }
}

/// <summary>Contains parsed notifications and whether the native buffer was malformed.</summary>
public sealed record DirectoryChangeParseResult(IReadOnlyList<DirectoryChangeNotification> Notifications, bool IsMalformed);
