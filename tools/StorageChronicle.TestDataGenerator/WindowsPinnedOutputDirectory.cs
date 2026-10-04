using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StorageChronicle.TestDataGenerator;

/// <summary>Pins a local NTFS output directory chain while a new fixture file is created.</summary>
internal sealed class WindowsPinnedOutputDirectory : IDisposable
{
    private const uint FileListDirectory = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const int FileAttributeTagInfo = 9;
    private const int DriveFixed = 3;
    private const uint VolumeNameGuid = 0x1;
    private const uint ObjectCaseInsensitive = 0x40;
    private const uint FileCreate = 2;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;

    private readonly List<SafeFileHandle> directoryHandles;

    private WindowsPinnedOutputDirectory(List<SafeFileHandle> directoryHandles) => this.directoryHandles = directoryHandles;

    /// <summary>Creates and opens a new direct child relative to the pinned parent handle.</summary>
    internal FileStream CreateNewFile(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName is "." or ".." || fileName.IndexOfAny(['\\', '/', ':', '\0']) >= 0 || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            throw new ArgumentException("Only a single direct-child output filename is accepted.", nameof(fileName));

        var parentHandle = directoryHandles[^1];
        var parentReference = false;
        var nameBuffer = IntPtr.Zero;
        var unicodeNamePointer = IntPtr.Zero;
        var attributesPointer = IntPtr.Zero;
        try
        {
            parentHandle.DangerousAddRef(ref parentReference);
            nameBuffer = Marshal.StringToHGlobalUni(fileName);
            var unicodeName = new UnicodeString
            {
                Length = checked((ushort)(fileName.Length * sizeof(char))),
                MaximumLength = checked((ushort)((fileName.Length + 1) * sizeof(char))),
                Buffer = nameBuffer
            };
            unicodeNamePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeName, unicodeNamePointer, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parentHandle.DangerousGetHandle(),
                ObjectName = unicodeNamePointer,
                Attributes = ObjectCaseInsensitive
            };
            attributesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<ObjectAttributes>());
            Marshal.StructureToPtr(attributes, attributesPointer, false);

            var status = NtCreateFile(out var outputHandle, FileWriteData | FileReadAttributes | Synchronize,
                attributesPointer, out _, IntPtr.Zero, FileAttributeNormal, FileShareRead,
                FileCreate, FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint, IntPtr.Zero, 0);
            if (status < 0)
            {
                if (status == unchecked((int)0xC0000035))
                    throw new IOException($"The output name '{fileName}' is already occupied; existing content was preserved.");
                var error = unchecked((int)RtlNtStatusToDosError(unchecked((uint)status)));
                throw new Win32Exception(error, $"The new output file could not be created relative to its pinned parent (NTSTATUS 0x{status:X8}).");
            }

            try
            {
                if (!GetFileInformationByHandleEx(outputHandle, FileAttributeTagInfo, out var info, (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The newly created output file attributes could not be verified.");
                if ((info.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
                    throw new IOException("The new output must be an ordinary file, not a directory or reparse point.");
                return new FileStream(outputHandle, FileAccess.Write, 4096, isAsync: false);
            }
            catch
            {
                outputHandle.Dispose();
                throw;
            }
        }
        finally
        {
            if (attributesPointer != IntPtr.Zero) Marshal.FreeHGlobal(attributesPointer);
            if (unicodeNamePointer != IntPtr.Zero) Marshal.FreeHGlobal(unicodeNamePointer);
            if (nameBuffer != IntPtr.Zero) Marshal.FreeHGlobal(nameBuffer);
            if (parentReference) parentHandle.DangerousRelease();
        }
    }

    /// <summary>Opens every directory from the volume root to the file's parent without delete sharing.</summary>
    internal static WindowsPinnedOutputDirectory OpenForFile(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("File output requires Windows handle-pinned local NTFS directories; use stdout on this platform.");

        var fullPath = Path.GetFullPath(outputPath);
        var parentPath = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("The output parent directory is missing.", nameof(outputPath));
        var volumeRoot = Path.GetPathRoot(parentPath);
        if (string.IsNullOrEmpty(volumeRoot) || volumeRoot.Length != 3 || !char.IsAsciiLetter(volumeRoot[0]) || volumeRoot[1] != ':' || volumeRoot[2] is not ('\\' or '/'))
            throw new IOException("File output is restricted to a local drive-letter volume.");
        volumeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(volumeRoot)) + Path.DirectorySeparatorChar;
        if (GetDriveType(volumeRoot) != DriveFixed) throw new IOException("File output requires a local fixed volume.");
        var volumeName = new char[64];
        var fileSystemName = new char[32];
        if (!GetVolumeInformation(volumeRoot, volumeName, volumeName.Length, out _, out _, out _, fileSystemName, fileSystemName.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The output volume filesystem could not be inspected.");
        if (!string.Equals(new string(fileSystemName).TrimEnd('\0'), "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("File output requires a local NTFS volume.");

        var handles = new List<SafeFileHandle>();
        try
        {
            var rootHandle = OpenDirectory(volumeRoot);
            handles.Add(rootHandle);
            var expectedFinalPath = GetFinalPath(rootHandle);
            var relativeParent = Path.GetRelativePath(volumeRoot, parentPath);
            var currentPath = volumeRoot;
            if (relativeParent != ".")
            {
                foreach (var component in relativeParent.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    currentPath = Path.Combine(currentPath, component);
                    var childHandle = OpenDirectory(currentPath);
                    handles.Add(childHandle);
                    expectedFinalPath = expectedFinalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar + component;
                    var actualFinalPath = GetFinalPath(childHandle);
                    if (!string.Equals(actualFinalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), expectedFinalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"An output directory resolved to a different path or volume than requested (expected '{expectedFinalPath}', actual '{actualFinalPath}').");
                }
            }
            return new WindowsPinnedOutputDirectory(handles);
        }
        catch
        {
            DisposeHandles(handles);
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose() => DisposeHandles(directoryHandles);

    private static SafeFileHandle OpenDirectory(string path)
    {
        var handle = CreateFile(path, FileListDirectory | FileReadAttributes | Synchronize,
            FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "An output directory could not be pinned without delete sharing.");
        }

        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out var info, (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "The pinned output directory attributes could not be verified.");
        }
        if ((info.FileAttributes & FileAttributeDirectory) == 0 || (info.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            handle.Dispose();
            throw new IOException("Output directories must be ordinary directories, not reparse points.");
        }
        return handle;
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[4096];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, VolumeNameGuid);
        if (length == 0 || length >= buffer.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The output directory's final volume path could not be resolved.");
        return new string(buffer, 0, checked((int)length));
    }

    private static void DisposeHandles(List<SafeFileHandle> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
        handles.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal int Length;
        internal IntPtr RootDirectory;
        internal IntPtr ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor;
        internal IntPtr SecurityQualityOfService;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint pathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass, out FileAttributeTagInformation information, uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDriveTypeW", SetLastError = true)]
    private static extern int GetDriveType(string rootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetVolumeInformationW", SetLastError = true)]
    private static extern bool GetVolumeInformation(string rootPathName, [Out] char[] volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, [Out] char[] fileSystemNameBuffer, int fileSystemNameSize);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle fileHandle, uint desiredAccess, IntPtr objectAttributes, out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions, IntPtr extendedAttributes, uint extendedAttributesLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(uint status);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal IntPtr Status;
        internal IntPtr Information;
    }

    private const uint FileWriteData = 0x00000002;
}
