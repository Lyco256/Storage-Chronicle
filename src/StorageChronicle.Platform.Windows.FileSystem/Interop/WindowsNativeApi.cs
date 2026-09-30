using System.ComponentModel;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.FileSystem.Interop;

internal sealed class WindowsNativeApi : IWindowsVolumeNative, IWindowsFileMetadataNative, IWindowsDirectoryChangeNative, IWindowsDeviceNotificationNative
{
    private const uint FileListDirectory = 0x0001;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileNotifyChangeFileName = 0x00000001;
    private const uint FileNotifyChangeDirName = 0x00000002;
    private const uint FileNotifyChangeAttributes = 0x00000004;
    private const uint FileNotifyChangeSize = 0x00000008;
    private const uint FileNotifyChangeLastWrite = 0x00000010;
    private const uint FileNotifyChangeCreation = 0x00000040;
    private const uint FileNotifyChangeSecurity = 0x00000100;
    private const uint FileReadAttributes = 0x00000080;
    private const int FileBasicInfoClass = 0;
    private const int FileStandardInfoClass = 1;
    private const int FileAttributeTagInfoClass = 9;
    private const int FileIdBothDirectoryInfoClass = 10;
    private const int FileIdBothDirectoryRestartInfoClass = 11;
    private const int ErrorNoMoreFiles = 18;
    private const int DirectoryBufferSize = 64 * 1024;
    private const int DirectoryEntryNameOffset = 104;
    private const uint ErrorNotifyEnumDir = 1022;
    private const uint ErrorInvalidHandle = 6;
    private const uint ErrorDeviceNotConnected = 1167;
    private const uint ErrorPathNotFound = 3;
    private const uint ErrorFileNotFound = 2;
    private const int ErrorIoPending = 997;
    private const int ErrorOperationAborted = 995;
    private const uint CmNotifyFilterTypeAll = 0;
    private const uint CmNotifyActionDeviceInterfaceArrival = 0;
    private const uint CmNotifyActionDeviceInterfaceRemoval = 1;

    public IReadOnlyList<NativeVolumeRecord> EnumerateVolumes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<NativeVolumeRecord>();
        }

        var result = new List<NativeVolumeRecord>();
        var volumeName = new string('\0', 1024);
        var findHandle = FindFirstVolume(volumeName, (uint)volumeName.Length);
        if (findHandle == new IntPtr(-1))
        {
            ThrowLastWin32Exception("FindFirstVolumeW");
        }

        try
        {
            while (true)
            {
                var guidPath = volumeName.TrimEnd('\0');
                result.Add(ReadVolume(guidPath));
                volumeName = new string('\0', 1024);
                if (!FindNextVolume(findHandle, volumeName, (uint)volumeName.Length))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 18)
                    {
                        break;
                    }

                    throw new Win32Exception(error, "FindNextVolumeW failed.");
                }
            }
        }
        finally
        {
            FindVolumeClose(findHandle);
        }

        return result;
    }

    public NativeFileMetadataRecord ReadMetadata(string path, string? parentPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows file metadata is unavailable on this operating system.");
        }

        using var handle = OpenMetadataPath(path);
        return ReadMetadata(handle, path, parentPath);
    }

    public NativeFileMetadataRecord ReadMetadata(SafeFileHandle handle, string path, string? parentPath = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureHandle(handle);
        var basic = new FileBasicInfo();
        var standard = new FileStandardInfo();
        var attributeTag = new FileAttributeTagInfo();
        if (!GetFileInformationByHandleEx(handle, FileBasicInfoClass, ref basic, (uint)Marshal.SizeOf<FileBasicInfo>()) ||
            !GetFileInformationByHandleEx(handle, FileStandardInfoClass, ref standard, (uint)Marshal.SizeOf<FileStandardInfo>()) ||
            !GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, ref attributeTag, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
        {
            ThrowLastWin32Exception("GetFileInformationByHandleEx metadata");
        }

        var attributes = (FileAttributes)attributeTag.FileAttributes;
        var kind = GetFileKind(attributes, attributeTag.ReparseTag);
        var fileId = TryReadFileId(handle, out var id) ? new FileId(id) : new FileId("path:" + path);
        FileId? parentId = null;
        if (!string.IsNullOrWhiteSpace(parentPath))
        {
            using var parentHandle = OpenMetadataPath(parentPath);
            if (TryReadFileId(parentHandle, out var parentValue)) parentId = new FileId(parentValue);
        }

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = path;
        return new NativeFileMetadataRecord(
            fileId,
            parentId,
            name,
            kind,
            kind == FileKind.Directory ? null : standard.EndOfFile,
            standard.AllocationSize,
            FromFileTime(basic.CreationTime),
            FromFileTime(basic.LastAccessTime),
            FromFileTime(basic.LastWriteTime),
            FromFileTime(basic.ChangeTime),
            attributes,
            (attributes & FileAttributes.ReparsePoint) != 0 ? kind.ToString() : null,
            true,
            false);
    }

    public IEnumerable<NativeDirectoryEntry> EnumerateDirectory(SafeFileHandle directoryHandle)
    {
        ArgumentNullException.ThrowIfNull(directoryHandle);
        EnsureHandle(directoryHandle);
        var informationClass = FileIdBothDirectoryRestartInfoClass;
        while (true)
        {
            var buffer = new byte[DirectoryBufferSize];
            var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            bool succeeded;
            int error;
            try
            {
                succeeded = GetFileInformationByHandleEx(directoryHandle, informationClass, pinned.AddrOfPinnedObject(), (uint)buffer.Length);
                error = succeeded ? 0 : Marshal.GetLastWin32Error();
            }
            finally
            {
                pinned.Free();
            }

            informationClass = FileIdBothDirectoryInfoClass;
            if (!succeeded)
            {
                if (error == ErrorNoMoreFiles) yield break;
                throw new Win32Exception(error, "Directory entries could not be read from the opened directory handle.");
            }

            foreach (var entry in ParseDirectoryEntries(buffer)) yield return entry;
        }
    }

    public SafeFileHandle OpenDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows directory handles are unavailable on this operating system.");
        }

        var handle = CreateFile(path, FileListDirectory, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint | FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"The directory could not be opened: {path}");
        }

        var tag = new FileAttributeTagInfo();
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, ref tag, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "The directory handle could not be verified.");
        }

        if (((FileAttributes)tag.FileAttributes & FileAttributes.Directory) == 0 ||
            ((FileAttributes)tag.FileAttributes & FileAttributes.ReparsePoint) != 0)
        {
            handle.Dispose();
            throw new IOException("The final path component is not a traversable non-reparse directory.");
        }

        return handle;
    }

    public async ValueTask<NativeDirectoryChangeReadResult> ReadChangesAsync(SafeFileHandle directoryHandle, int bufferSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directoryHandle);
        if (directoryHandle.IsInvalid)
        {
            return new NativeDirectoryChangeReadResult(Array.Empty<byte>(), 0, (int)ErrorInvalidHandle);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var buffer = new byte[bufferSize];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        using var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
        var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlappedData>());
        var completionHandle = completed.SafeWaitHandle;
        var completionHandleReferenced = false;
        try
        {
            completionHandle.DangerousAddRef(ref completionHandleReferenced);
            Marshal.StructureToPtr(new NativeOverlappedData { EventHandle = completionHandle.DangerousGetHandle() }, overlapped, false);
            using var cancellation = cancellationToken.Register(() => CancelIoEx(directoryHandle, overlapped));
            var succeeded = ReadDirectoryChangesWOverlapped(directoryHandle, pinned.AddrOfPinnedObject(), (uint)buffer.Length, true, FileNotifyChangeFileName | FileNotifyChangeDirName | FileNotifyChangeAttributes | FileNotifyChangeSize | FileNotifyChangeLastWrite | FileNotifyChangeCreation | FileNotifyChangeSecurity, IntPtr.Zero, overlapped, IntPtr.Zero);
            var error = succeeded ? 0 : Marshal.GetLastWin32Error();
            if (!succeeded && error != ErrorIoPending)
            {
                if (error == ErrorOperationAborted && cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                return new NativeDirectoryChangeReadResult(buffer, 0, error);
            }

            if (cancellationToken.IsCancellationRequested) CancelIoEx(directoryHandle, overlapped);
            await Task.Run(() => completed.WaitOne(), CancellationToken.None).ConfigureAwait(false);
            if (!GetOverlappedResult(directoryHandle, overlapped, out var bytesReturned, false))
            {
                error = Marshal.GetLastWin32Error();
                if (error == ErrorOperationAborted && cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                return new NativeDirectoryChangeReadResult(buffer, 0, error);
            }

            return new NativeDirectoryChangeReadResult(buffer, checked((int)bytesReturned), 0);
        }
        finally
        {
            if (completionHandleReferenced) completionHandle.DangerousRelease();
            Marshal.FreeHGlobal(overlapped);
            pinned.Free();
        }
    }

    public IDisposable Register(Action<ExternalMediaChangeKind> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!OperatingSystem.IsWindows())
        {
            return NoopDisposable.Instance;
        }

        var registration = new DeviceNotificationRegistration(callback);
        registration.Start();
        return registration;
    }

    private NativeVolumeRecord ReadVolume(string guidPath)
    {
        var mountPoints = GetMountPoints(guidPath);
        var volumeRoot = guidPath.EndsWith(Path.DirectorySeparatorChar) ? guidPath : guidPath + Path.DirectorySeparatorChar;
        var driveTypeRoot = mountPoints.Length == 0 ? volumeRoot : mountPoints[0];
        var fileSystem = new string('\0', 256);
        var volumeName = new string('\0', 256);
        var flags = 0u;
        var serial = 0u;
        var maximumComponentLength = 0u;
        var infoSucceeded = GetVolumeInformation(volumeRoot, volumeName, (uint)volumeName.Length, ref serial, ref maximumComponentLength, ref flags, fileSystem, (uint)fileSystem.Length);
        if (!infoSucceeded)
        {
            fileSystem = string.Empty;
        }

        var readable = CanEnumerateDirectory(volumeRoot);
        var driveType = GetDriveType(driveTypeRoot);
        return new NativeVolumeRecord(guidPath, fileSystem.TrimEnd('\0'), mountPoints, driveType, (flags & 0x00080000) != 0, readable, string.Equals(fileSystem.TrimEnd('\0'), "NTFS", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] GetMountPoints(string guidPath)
    {
        var buffer = new string('\0', 4096);
        if (!GetVolumePathNamesForVolumeName(guidPath, buffer, (uint)buffer.Length, out var requiredLength))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 234 || requiredLength == 0)
            {
                return Array.Empty<string>();
            }

            buffer = new string('\0', checked((int)requiredLength + 1));
            if (!GetVolumePathNamesForVolumeName(guidPath, buffer, (uint)buffer.Length, out _))
            {
                return Array.Empty<string>();
            }
        }

        return buffer.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private bool CanEnumerateDirectory(string root)
    {
        try
        {
            using var handle = OpenDirectory(root);
            using var enumerator = EnumerateDirectory(handle).GetEnumerator();
            _ = enumerator.MoveNext();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static FileKind GetFileKind(FileAttributes attributes, uint reparseTag)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return (attributes & FileAttributes.Directory) != 0 ? FileKind.Directory : FileKind.File;
        }

        if (reparseTag == 0xA000000C)
        {
            return FileKind.SymbolicLink;
        }

        return reparseTag == 0xA0000003 ? FileKind.Junction : FileKind.ReparsePoint;
    }

    private static SafeFileHandle OpenMetadataPath(string path)
    {
        var handle = CreateFile(path, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Metadata could not be read without following the final path component: {path}");
        }

        return handle;
    }

    private static DateTimeOffset? FromFileTime(long value) => value <= 0 ? null : DateTimeOffset.FromFileTime(value);

    private static bool TryReadFileId(SafeFileHandle handle, out string id)
    {
        id = string.Empty;
        var info = new FileIdInfo();
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, ref info, (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            return false;
        }

        id = $"{info.VolumeSerialNumber:X16}:{Convert.ToHexString(info.FileId)}";
        return true;
    }

    private static void EnsureHandle(SafeFileHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed) throw new IOException("The file-system handle is not available.");
    }

    private static IEnumerable<NativeDirectoryEntry> ParseDirectoryEntries(byte[] buffer)
    {
        const int fixedHeaderLength = DirectoryEntryNameOffset;
        var offset = 0;
        while (offset <= buffer.Length - fixedHeaderLength)
        {
            var nextOffset = BitConverter.ToUInt32(buffer, offset);
            var nameLength = BitConverter.ToUInt32(buffer, offset + 60);
            if ((nameLength & 1) != 0 || nameLength > buffer.Length - offset - fixedHeaderLength)
            {
                throw new IOException("The native directory-entry buffer is malformed.");
            }

            var name = Encoding.Unicode.GetString(buffer, offset + fixedHeaderLength, checked((int)nameLength));
            var attributes = (FileAttributes)BitConverter.ToUInt32(buffer, offset + 56);
            if (name is not "." and not "..") yield return new NativeDirectoryEntry(name, attributes);
            if (nextOffset == 0) yield break;
            if (nextOffset < fixedHeaderLength || nextOffset > buffer.Length - offset || (nextOffset & 7) != 0)
            {
                throw new IOException("The native directory-entry chain is malformed.");
            }

            offset = checked(offset + (int)nextOffset);
        }

        throw new IOException("The native directory-entry buffer ended before its final entry.");
    }

    private static void ThrowLastWin32Exception(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), operation + " failed.");

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }

    private sealed class DeviceNotificationRegistration : IDisposable
    {
        private readonly Action<ExternalMediaChangeKind> callback;
        private readonly CmNotifyCallback callbackDelegate;
        private IntPtr handle;
        private bool disposed;

        public DeviceNotificationRegistration(Action<ExternalMediaChangeKind> callback)
        {
            this.callback = callback;
            callbackDelegate = OnNotification;
        }

        public void Start()
        {
            var filter = new CmNotifyFilter { Size = (uint)Marshal.SizeOf<CmNotifyFilter>(), Type = CmNotifyFilterTypeAll };
            var result = CmRegisterNotification(ref filter, IntPtr.Zero, callbackDelegate, out handle);
            if (result != 0)
            {
                throw new Win32Exception(checked((int)result), "CM_Register_Notification failed.");
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (handle != IntPtr.Zero && CmUnregisterNotification(handle) != 0) handle = IntPtr.Zero;
            GC.KeepAlive(callbackDelegate);
        }

        private uint OnNotification(IntPtr hNotify, IntPtr context, uint action, IntPtr eventData, uint eventDataSize)
        {
            if (action == CmNotifyActionDeviceInterfaceArrival) callback(ExternalMediaChangeKind.Connected);
            else if (action == CmNotifyActionDeviceInterfaceRemoval) callback(ExternalMediaChangeKind.Disconnected);
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CmNotifyFilter { public uint Size; public uint Type; public Guid Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo { public ulong VolumeSerialNumber; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] FileId; }

    private const int FileIdInfoClass = 18;
    private delegate uint CmNotifyCallback(IntPtr hNotify, IntPtr context, uint action, IntPtr eventData, uint eventDataSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr FindFirstVolume(string volumeName, uint bufferLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool FindNextVolume(IntPtr volumeFindHandle, string volumeName, uint bufferLength);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FindVolumeClose(IntPtr volumeFindHandle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetVolumePathNamesForVolumeName(string volumeName, string volumePathNames, uint bufferLength, out uint returnLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetVolumeInformation(string rootPathName, string volumeNameBuffer, uint volumeNameSize, ref uint volumeSerialNumber, ref uint maximumComponentLength, ref uint fileSystemFlags, string fileSystemNameBuffer, uint fileSystemNameSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern DriveType GetDriveType(string rootPathName);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", EntryPoint = "ReadDirectoryChangesW", SetLastError = true)] private static extern bool ReadDirectoryChangesWOverlapped(SafeFileHandle directoryHandle, IntPtr buffer, uint bufferLength, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree, uint notifyFilter, IntPtr bytesReturned, IntPtr overlapped, IntPtr completionRoutine);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(SafeFileHandle fileHandle, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(SafeFileHandle fileHandle, IntPtr overlapped, out uint bytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle fileHandle, int fileInformationClass, ref FileIdInfo fileInformation, uint bufferSize);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle fileHandle, int fileInformationClass, ref FileBasicInfo fileInformation, uint bufferSize);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle fileHandle, int fileInformationClass, ref FileStandardInfo fileInformation, uint bufferSize);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle fileHandle, int fileInformationClass, ref FileAttributeTagInfo fileInformation, uint bufferSize);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle fileHandle, int fileInformationClass, IntPtr fileInformation, uint bufferSize);
    [DllImport("Cfgmgr32.dll", SetLastError = false)] private static extern uint CmRegisterNotification(ref CmNotifyFilter filter, IntPtr context, CmNotifyCallback callback, out IntPtr notifyContext);
    [DllImport("Cfgmgr32.dll", SetLastError = false)] private static extern uint CmUnregisterNotification(IntPtr notifyContext);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo { public long CreationTime; public long LastAccessTime; public long LastWriteTime; public long ChangeTime; public uint FileAttributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo { public long AllocationSize; public long EndOfFile; public uint NumberOfLinks; public byte DeletePending; public byte Directory; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { public uint FileAttributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlappedData { public IntPtr Internal; public IntPtr InternalHigh; public uint Offset; public uint OffsetHigh; public IntPtr EventHandle; }
}

/// <summary>Windows drive type values returned by GetDriveTypeW.</summary>
public enum DriveType : uint
{
    /// <summary>The drive type could not be determined.</summary>
    Unknown = 0,
    /// <summary>The root path is invalid.</summary>
    NoRootDirectory = 1,
    /// <summary>Removable media.</summary>
    Removable = 2,
    /// <summary>Fixed local media.</summary>
    Fixed = 3,
    /// <summary>Remote media.</summary>
    Remote = 4,
    /// <summary>Optical media.</summary>
    CdRom = 5,
    /// <summary>RAM disk.</summary>
    RamDisk = 6
}
