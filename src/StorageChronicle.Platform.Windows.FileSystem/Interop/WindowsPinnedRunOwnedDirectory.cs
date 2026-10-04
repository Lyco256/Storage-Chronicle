using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StorageChronicle.Platform.Windows.FileSystem.Interop;

/// <summary>Pins an existing NTFS fixture directory and performs only direct-child read/new-file operations relative to its handle.</summary>
internal sealed class WindowsPinnedRunOwnedDirectory : IDisposable
{
    private const uint FileListDirectory = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileOpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint ObjectCaseInsensitive = 0x40;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int FileAttributeTagInfo = 9;

    private readonly SafeFileHandle directoryHandle;

    private WindowsPinnedRunOwnedDirectory(string directoryPath, string volumeUniqueId, SafeFileHandle directoryHandle)
    {
        DirectoryPath = directoryPath;
        VolumeUniqueId = volumeUniqueId;
        this.directoryHandle = directoryHandle;
    }

    internal string DirectoryPath { get; }
    internal string VolumeUniqueId { get; }

    /// <summary>Opens and pins an existing, non-reparse NTFS directory on a volume-GUID-addressable local volume.</summary>
    internal static WindowsPinnedRunOwnedDirectory OpenExisting(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run-owned evidence directories require Windows handle-relative I/O.");

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("The run-owned evidence directory does not exist.");
        EnsureNoReparsePoints(fullPath);

        var mountBuffer = new char[1024];
        if (!GetVolumePathName(fullPath, mountBuffer, (uint)mountBuffer.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The evidence directory mount root could not be resolved.");
        var mountRoot = new string(mountBuffer).TrimEnd('\0');
        if (!Path.EndsInDirectorySeparator(mountRoot)) mountRoot += Path.DirectorySeparatorChar;

        var volumeBuffer = new char[1024];
        if (!GetVolumeNameForVolumeMountPoint(mountRoot, volumeBuffer, (uint)volumeBuffer.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The evidence volume GUID could not be resolved.");
        var volumeUniqueId = new string(volumeBuffer).TrimEnd('\0');
        if (!volumeUniqueId.StartsWith("\\\\?\\Volume{", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The evidence root is not on a volume-GUID-addressable local volume.");
        if (!IsNtfs(volumeUniqueId)) throw new IOException("Run-owned evidence must reside on NTFS.");

        var handle = CreateFile(fullPath, FileReadAttributes | FileListDirectory | Synchronize,
            FileShareRead | FileShareWrite, IntPtr.Zero, FileOpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "The evidence directory could not be pinned.");
        }

        try
        {
            EnsureDirectoryHandle(handle);
            var finalPath = GetFinalPath(handle);
            var relative = Path.GetRelativePath(mountRoot, fullPath);
            var expectedFinalPath = relative == "." ? volumeUniqueId : Path.Combine(volumeUniqueId, relative);
            if (!string.Equals(Path.TrimEndingDirectorySeparator(finalPath), Path.TrimEndingDirectorySeparator(expectedFinalPath), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The pinned evidence directory resolved to a different path or volume than requested.");
            return new WindowsPinnedRunOwnedDirectory(fullPath, volumeUniqueId, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Opens a direct child without following a reparse point.</summary>
    internal FileStream OpenReadDirectChild(string name)
    {
        var handle = OpenDirectChild(name, FileReadAttributes | 0x0001 | Synchronize, FileShareRead | FileShareWrite, FileOpen);
        try
        {
            EnsureOrdinaryFile(handle);
            return new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Creates a direct child without replacement and returns its already-open write-only handle.</summary>
    internal FileStream CreateNewDirectChild(string name)
    {
        var handle = OpenDirectChild(name, 0x0002 | FileReadAttributes | Synchronize, FileShareRead, FileCreate);
        try
        {
            EnsureOrdinaryFile(handle);
            return new FileStream(handle, FileAccess.Write, 64 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose() => directoryHandle.Dispose();

    private SafeFileHandle OpenDirectChild(string name, uint desiredAccess, uint shareAccess, uint disposition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or ".." || name.IndexOfAny(['\\', '/', ':', '\0']) >= 0 || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            throw new ArgumentException("Only a single direct-child filename is allowed.", nameof(name));

        var parentReference = false;
        var nameBuffer = IntPtr.Zero;
        var unicodeNamePointer = IntPtr.Zero;
        var attributesPointer = IntPtr.Zero;
        try
        {
            directoryHandle.DangerousAddRef(ref parentReference);
            nameBuffer = Marshal.StringToHGlobalUni(name);
            var unicodeName = new UnicodeString
            {
                Length = checked((ushort)(name.Length * sizeof(char))),
                MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
                Buffer = nameBuffer
            };
            unicodeNamePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeName, unicodeNamePointer, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = directoryHandle.DangerousGetHandle(),
                ObjectName = unicodeNamePointer,
                Attributes = ObjectCaseInsensitive
            };
            attributesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<ObjectAttributes>());
            Marshal.StructureToPtr(attributes, attributesPointer, false);

            var status = NtCreateFile(out var rawHandle, desiredAccess, attributesPointer, out _, IntPtr.Zero,
                FileAttributeNormal, shareAccess, disposition,
                FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint, IntPtr.Zero, 0);
            if (status < 0)
            {
                if (status == unchecked((int)0xC0000034))
                    throw new FileNotFoundException("The run-owned direct child does not exist.", name);
                if (status == unchecked((int)0xC000003A))
                    throw new DirectoryNotFoundException("The run-owned direct-child path does not exist.");
                if (status is unchecked((int)0xC0000035) or unchecked((int)0xC00000BA))
                    throw new IOException("The run-owned direct-child name is already occupied; existing content was preserved.");
                var error = unchecked((int)RtlNtStatusToDosError(status));
                throw new Win32Exception(error, $"The run-owned direct-child operation failed (NTSTATUS 0x{status:X8}).");
            }
            return new SafeFileHandle(rawHandle, ownsHandle: true);
        }
        finally
        {
            if (attributesPointer != IntPtr.Zero) Marshal.FreeHGlobal(attributesPointer);
            if (unicodeNamePointer != IntPtr.Zero) Marshal.FreeHGlobal(unicodeNamePointer);
            if (nameBuffer != IntPtr.Zero) Marshal.FreeHGlobal(nameBuffer);
            if (parentReference) directoryHandle.DangerousRelease();
        }
    }

    private static void EnsureOrdinaryFile(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out var info, (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The direct-child file attributes could not be verified.");
        if ((info.FileAttributes & (FileAttributeReparsePoint | 0x10)) != 0)
            throw new IOException("Evidence files may not be directories or reparse points.");
    }

    private static void EnsureDirectoryHandle(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out var info, (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The evidence directory attributes could not be verified.");
        if ((info.FileAttributes & 0x10) == 0 || (info.FileAttributes & FileAttributeReparsePoint) != 0)
            throw new IOException("The evidence root must be an ordinary directory, not a reparse point.");
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Run-owned evidence refuses paths that traverse reparse points: {current}");
            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[4096];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0x1);
        if (length == 0 || length >= buffer.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "The evidence directory's final volume path could not be resolved.");
        return new string(buffer, 0, checked((int)length));
    }

    private static bool IsNtfs(string volumeGuidPath)
    {
        var volumeName = new char[256];
        var fileSystemName = new char[64];
        uint serial = 0;
        uint maximumComponentLength = 0;
        uint flags = 0;
        return GetVolumeInformation(volumeGuidPath, volumeName, (uint)volumeName.Length, ref serial,
                   ref maximumComponentLength, ref flags, fileSystemName, (uint)fileSystemName.Length) &&
               new string(fileSystemName).TrimEnd('\0').Equals("NTFS", StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation { public uint FileAttributes; public uint ReparseTag; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumePathNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, [Out] char[] volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, [Out] char[] volumeName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string rootPathName, [Out] char[] volumeNameBuffer, uint volumeNameSize,
        ref uint volumeSerialNumber, ref uint maximumComponentLength, ref uint fileSystemFlags,
        [Out] char[] fileSystemNameBuffer, uint fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] path, uint pathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileAttributeTagInformation info, uint bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out IntPtr handle, uint desiredAccess, IntPtr objectAttributes,
        out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess,
        uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
