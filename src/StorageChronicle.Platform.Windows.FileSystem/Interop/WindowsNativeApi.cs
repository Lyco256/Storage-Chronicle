using System.ComponentModel;
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
    private const uint FileNotifyChangeFileName = 0x00000001;
    private const uint FileNotifyChangeDirName = 0x00000002;
    private const uint FileNotifyChangeAttributes = 0x00000004;
    private const uint FileNotifyChangeSize = 0x00000008;
    private const uint FileNotifyChangeLastWrite = 0x00000010;
    private const uint FileNotifyChangeCreation = 0x00000040;
    private const uint FileNotifyChangeSecurity = 0x00000100;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ErrorNotifyEnumDir = 1022;
    private const uint ErrorInvalidHandle = 6;
    private const uint ErrorDeviceNotConnected = 1167;
    private const uint ErrorPathNotFound = 3;
    private const uint ErrorFileNotFound = 2;
    private const uint CmNotifyFilterFlagAllInterfaceClasses = 0x00000001;
    private const uint CmNotifyFilterTypeDeviceInterface = 0;
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

        var attributes = File.GetAttributes(path);
        var info = new FileInfo(path);
        var kind = GetFileKind(path, attributes);
        var fileId = TryReadFileId(path, out var id) ? new FileId(id) : new FileId("path:" + path);
        FileId? parentId = null;
        if (!string.IsNullOrWhiteSpace(parentPath) && TryReadFileId(parentPath, out var parentValue))
        {
            parentId = new FileId(parentValue);
        }

        return new NativeFileMetadataRecord(
            fileId,
            parentId,
            info.Name,
            kind,
            kind == FileKind.Directory ? null : TryGetLength(info),
            null,
            TryGetUtc(info.CreationTimeUtc),
            TryGetUtc(info.LastAccessTimeUtc),
            TryGetUtc(info.LastWriteTimeUtc),
            TryGetUtc(info.LastWriteTimeUtc),
            attributes,
            kind is FileKind.SymbolicLink or FileKind.Junction or FileKind.ReparsePoint ? kind.ToString() : null,
            true,
            false);
    }

    public SafeFileHandle OpenDirectory(string path)
    {
        return OpenMetadata(path, directory: true, FileListDirectory);
    }

    public SafeFileHandle OpenMetadata(string path, bool directory)
    {
        return OpenMetadata(path, directory, FileReadAttributes);
    }

    private static SafeFileHandle OpenMetadata(string path, bool directory, uint desiredAccess)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows metadata handles are unavailable on this operating system.");
        }

        var flags = directory ? FileFlagBackupSemantics : 0u;
        var handle = CreateFile(path, desiredAccess, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"The metadata entry could not be opened: {path}");
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

        var buffer = new byte[bufferSize];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var result = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var succeeded = ReadDirectoryChangesW(directoryHandle, pinned.AddrOfPinnedObject(), (uint)buffer.Length, true, FileNotifyChangeFileName | FileNotifyChangeDirName | FileNotifyChangeAttributes | FileNotifyChangeSize | FileNotifyChangeLastWrite | FileNotifyChangeCreation | FileNotifyChangeSecurity, out var bytesReturned, IntPtr.Zero, IntPtr.Zero);
                var error = succeeded ? 0 : Marshal.GetLastWin32Error();
                return new NativeDirectoryChangeReadResult(buffer, checked((int)bytesReturned), error);
            }, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            CancelIoEx(directoryHandle, IntPtr.Zero);
            throw;
        }
        finally
        {
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
        var root = mountPoints.Length == 0 ? guidPath : mountPoints[0];
        var fileSystem = new string('\0', 256);
        var volumeName = new string('\0', 256);
        var flags = 0u;
        var serial = 0u;
        var maximumComponentLength = 0u;
        var infoSucceeded = GetVolumeInformation(root, volumeName, (uint)volumeName.Length, ref serial, ref maximumComponentLength, ref flags, fileSystem, (uint)fileSystem.Length);
        if (!infoSucceeded)
        {
            fileSystem = string.Empty;
        }

        var readable = CanEnumerateDirectory(root);
        var driveType = GetDriveType(root);
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

    private static bool CanEnumerateDirectory(string root)
    {
        try
        {
            using var enumerator = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
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
    }

    private static FileKind GetFileKind(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return (attributes & FileAttributes.Directory) != 0 ? FileKind.Directory : FileKind.File;
        }

        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            return FileKind.SymbolicLink;
        }

        return (attributes & FileAttributes.Directory) != 0 ? FileKind.Junction : FileKind.ReparsePoint;
    }

    private static long? TryGetLength(FileInfo info)
    {
        try { return info.Length; } catch (UnauthorizedAccessException) { return null; } catch (IOException) { return null; }
    }

    private static DateTimeOffset? TryGetUtc(DateTime value) => value == DateTime.MinValue ? null : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static bool TryReadFileId(string path, out string id)
    {
        id = string.Empty;
        using var handle = CreateFile(path, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return false;
        }

        var info = new FileIdInfo();
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, ref info, (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            return false;
        }

        id = $"{info.VolumeSerialNumber:X16}:{Convert.ToHexString(info.FileId)}";
        return true;
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
            var filter = new CmNotifyFilter
            {
                Size = (uint)Marshal.SizeOf<CmNotifyFilter>(),
                Flags = CmNotifyFilterFlagAllInterfaceClasses,
                FilterType = CmNotifyFilterTypeDeviceInterface,
                Reserved = 0,
                DeviceInterface = new CmNotifyFilterDeviceInterface { ClassGuid = Guid.Empty }
            };
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

    [StructLayout(LayoutKind.Sequential)]
    private struct CmNotifyFilter
    {
        public uint Size;
        public uint Flags;
        public uint FilterType;
        public uint Reserved;
        public CmNotifyFilterUnion Union;

        public CmNotifyFilterDeviceInterface DeviceInterface
        {
            set => Union = new CmNotifyFilterUnion { DeviceInterface = value };
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 400)]
    private struct CmNotifyFilterUnion
    {
        [FieldOffset(0)] public CmNotifyFilterDeviceInterface DeviceInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CmNotifyFilterDeviceInterface
    {
        public Guid ClassGuid;
    }

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
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadDirectoryChangesW(SafeFileHandle directoryHandle, IntPtr buffer, uint bufferLength, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree, uint notifyFilter, out uint bytesReturned, IntPtr overlapped, IntPtr completionRoutine);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(SafeFileHandle fileHandle, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle fileHandle, int fileInformationClass, ref FileIdInfo fileInformation, uint bufferSize);
    [DllImport("Cfgmgr32.dll", EntryPoint = "CM_Register_Notification", SetLastError = false)] private static extern uint CmRegisterNotification(ref CmNotifyFilter filter, IntPtr context, CmNotifyCallback callback, out IntPtr notifyContext);
    [DllImport("Cfgmgr32.dll", EntryPoint = "CM_Unregister_Notification", SetLastError = false)] private static extern uint CmUnregisterNotification(IntPtr notifyContext);
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
