using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.FileSystem.Interop;

/// <summary>Performs directory-relative file creation and rename operations beneath a pinned Windows volume root.</summary>
public sealed class WindowsVolumeDirectorySession : IVolumeBoundMediaFileSystem
{
    private const uint FileListDirectory = 0x0001;
    private const uint FileTraverse = 0x0020;
    private const uint FileAddFile = 0x0002;
    private const uint FileAddSubdirectory = 0x0004;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileReadData = 0x0001;
    private const uint Delete = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint ObjectCaseInsensitive = 0x40;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileOpen = 1;
    private const uint FileOpenIf = 3;
    private const uint FileCreate = 2;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const uint FileOpenDisposition = 1;
    private const int FileRenameInformation = 10;
    private const int FileAttributeTagInfo = 9;
    private const int FileNamesInformation = 12;
    private const int StatusNoMoreFiles = unchecked((int)0x80000006);
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const string ProductDirectoryName = ".StorageChronicle";
    private const string OwnershipMarkerName = ".storage-chronicle-owner.json";
    private const string OwnershipSchema = "StorageChronicle.MediaOwnership.v1";
    private readonly string expectedVolumeGuidPath;
    private readonly SafeFileHandle volumeRoot;
    private SafeFileHandle? productRootHandle;
    private readonly bool rootHasCreateAccess;
    private readonly object sync = new();
    private readonly HashSet<Stream> movableStreams = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, SafeFileHandle> pinnedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private bool createdProductRoot;
    private bool disposed;

    private WindowsVolumeDirectorySession(string expectedVolumeGuidPath, SafeFileHandle volumeRoot, bool rootHasCreateAccess = false, SafeFileHandle? productRootHandle = null)
    {
        this.expectedVolumeGuidPath = NormalizeVolumeGuidPath(expectedVolumeGuidPath);
        this.volumeRoot = volumeRoot;
        this.rootHasCreateAccess = rootHasCreateAccess;
        this.productRootHandle = productRootHandle;
    }

    /// <inheritdoc />
    public VolumeId VolumeId => VolumeId.Create(expectedVolumeGuidPath.TrimEnd('\\').ToUpperInvariant());

    /// <summary>Opens and pins the root directory of one expected volume GUID path.</summary>
    /// <param name="volumeGuidPath">A Windows volume GUID root path, such as <c>\\?\Volume{...}\</c>.</param>
    /// <returns>A session whose child opens are rooted at the verified volume directory handle.</returns>
    public static WindowsVolumeDirectorySession Open(string volumeGuidPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeGuidPath);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Volume directory sessions are available only on Windows.");
        var expected = NormalizeVolumeGuidPath(volumeGuidPath);
        var handle = CreateFile(expected, FileReadAttributes | FileListDirectory | FileTraverse | Synchronize,
            FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"The expected volume GUID root could not be opened (Win32 {error}).");
        }

        SafeFileHandle? pinnedProductRoot = null;
        try
        {
            EnsureExpectedVolume(handle, expected);
            EnsureDirectoryNotReparsePoint(handle);
            if (TryOpenEntryHandle(handle, ProductDirectoryName, expected, out var productRootHandle))
            {
                using (productRootHandle)
                {
                    if ((GetAttributes(productRootHandle) & FileAttributes.Directory) == 0)
                        throw new IOException("The existing media storage path is not a directory.");
                    pinnedProductRoot = OpenDirectoryCore(handle, ProductDirectoryName, FileOpen, expected, FileAddFile | FileAddSubdirectory);
                    EnsureOwnedProductRoot(pinnedProductRoot, expected);
                }
            }
            return new WindowsVolumeDirectorySession(expected, handle, productRootHandle: pinnedProductRoot);
        }
        catch
        {
            pinnedProductRoot?.Dispose();
            handle.Dispose();
            throw;
        }
    }

    internal static WindowsVolumeDirectorySession OpenAtExistingDirectory(string volumeGuidPath, string volumeMountRoot, string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeGuidPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeMountRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Volume directory sessions are available only on Windows.");
        var expected = NormalizeVolumeGuidPath(volumeGuidPath);
        var mountRoot = Path.GetFullPath(volumeMountRoot);
        var fullDirectory = Path.GetFullPath(directoryPath);
        var relative = Path.GetRelativePath(mountRoot, fullDirectory);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The fixture directory must be beneath its reported volume mount root.", nameof(directoryPath));
        var handle = CreateFile(fullDirectory, FileReadAttributes | FileListDirectory | FileTraverse | FileAddFile | FileAddSubdirectory | Synchronize,
            FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "The isolated fixture directory could not be opened.");
        }
        try
        {
            EnsureExpectedVolume(handle, expected);
            EnsureDirectoryNotReparsePoint(handle);
            return new WindowsVolumeDirectorySession(expected, handle, rootHasCreateAccess: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Opens or creates one child directory relative to a verified parent directory handle.</summary>
    /// <param name="parent">The pinned volume root or a child handle returned by this session.</param>
    /// <param name="name">One directory-name component; separators and traversal components are rejected.</param>
    /// <returns>A verified handle to the child directory.</returns>
    internal SafeFileHandle OpenOrCreateDirectory(SafeFileHandle parent, string name)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(parent);
        ValidateComponent(name);
        EnsureExpectedVolume(parent, expectedVolumeGuidPath);
        return OpenDirectoryCore(parent, name, FileOpenIf, expectedVolumeGuidPath);
    }

    /// <summary>Opens one existing child directory relative to a verified parent directory handle.</summary>
    /// <param name="parent">The pinned volume root or a child handle returned by this session.</param>
    /// <param name="name">One directory-name component; separators and traversal components are rejected.</param>
    /// <returns>A verified handle to the existing child directory.</returns>
    internal SafeFileHandle OpenDirectory(SafeFileHandle parent, string name)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(parent);
        ValidateComponent(name);
        EnsureExpectedVolume(parent, expectedVolumeGuidPath);
        return OpenDirectoryCore(parent, name, FileOpen, expectedVolumeGuidPath);
    }

    /// <inheritdoc />
    public void EnsureDirectory(string relativePath)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            var components = SplitRelativePath(relativePath, allowEmpty: true);
            EnsureOwnedMediaRoot();
            using var directory = OpenDirectoryPath(components, createMissing: true);
        }
    }

    /// <inheritdoc />
    public bool TryCreateDirectory(string relativePath)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            var (directories, leaf) = SplitFilePath(relativePath);
            if (!IsProductRootPath(relativePath)) EnsureOwnedMediaRoot();
            using var parent = OpenDirectoryPath(directories, createMissing: false, finalAccess: FileAddSubdirectory);
            try
            {
                if (IsProductRootPath(relativePath))
                {
                    var created = OpenDirectoryCore(parent, leaf, FileCreate, expectedVolumeGuidPath);
                    productRootHandle = created;
                    createdProductRoot = true;
                }
                else
                {
                    using var created = OpenDirectoryCore(parent, leaf, FileCreate, expectedVolumeGuidPath);
                    var components = SplitRelativePath(relativePath, allowEmpty: false);
                    var cacheKey = string.Join('\\', components.Skip(1));
                    if (pinnedDirectories.TryGetValue(cacheKey, out var previous)) previous.Dispose();
                    pinnedDirectories[cacheKey] = DuplicateHandleCore(created, "The new media directory handle could not be pinned.");
                }
                return true;
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode is ErrorFileExists or ErrorAlreadyExists)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public bool DirectoryExists(string relativePath)
    {
        ThrowIfDisposed();
        var components = SplitRelativePath(relativePath, allowEmpty: true);
        if (components.Count > 1) EnsureOwnedMediaRoot();
        try
        {
            using var directory = OpenDirectoryPath(components, createMissing: false);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    /// <inheritdoc />
    public bool FileExists(string relativePath)
    {
        ThrowIfDisposed();
        var (directories, leaf) = SplitFilePath(relativePath);
        if (!IsOwnershipMarkerPath(relativePath)) EnsureOwnedMediaRoot();
        using var parent = OpenDirectoryPath(directories, createMissing: false);
        try
        {
            using var entry = OpenEntryHandle(parent, leaf);
            return (GetAttributes(entry) & FileAttributes.Directory) == 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaFileSystemEntry> EnumerateEntries(string relativeDirectory)
    {
        ThrowIfDisposed();
        var components = SplitRelativePath(relativeDirectory, allowEmpty: true);
        EnsureOwnedMediaRoot();
        using var directory = OpenDirectoryPath(components, createMissing: false);
        var results = new List<MediaFileSystemEntry>();
        foreach (var name in EnumerateNames(directory))
        {
            if (name is "." or "..") continue;
            ValidateComponent(name);
            using var entry = OpenEntryHandle(directory, name);
            results.Add(new MediaFileSystemEntry(name, GetAttributes(entry)));
        }

        return results;
    }

    /// <inheritdoc />
    public Stream OpenRead(string relativePath)
    {
        ThrowIfDisposed();
        var (directories, leaf) = SplitFilePath(relativePath);
        if (!IsOwnershipMarkerPath(relativePath)) EnsureOwnedMediaRoot();
        using var parent = OpenDirectoryPath(directories, createMissing: false);
        var handle = OpenEntryHandle(parent, leaf, FileReadData | FileReadAttributes | Synchronize, FileShareRead);
        try
        {
            if ((GetAttributes(handle) & FileAttributes.Directory) != 0) throw new IOException("A directory cannot be opened as a media file.");
            return new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public Stream OpenTemporaryForRecovery(string relativePath)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            var (directories, leaf) = SplitFilePath(relativePath);
            if (!IsOwnershipMarkerPath(relativePath)) EnsureOwnedMediaRoot();
            if (directories.Count != 3 || !directories[1].Equals("writers", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileNameWithoutExtension(leaf), "N", out _) || !leaf.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only a GUID-named writer segment temporary can be opened for recovery.", nameof(relativePath));
            using var parent = OpenDirectoryPath(directories, createMissing: false);
            var handle = OpenEntryHandle(parent, leaf, FileReadData | FileReadAttributes | Delete | Synchronize, FileShareRead | FileShareDelete);
            try
            {
                if ((GetAttributes(handle) & FileAttributes.Directory) != 0) throw new IOException("A directory cannot be recovered as a media file.");
                var stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
                movableStreams.Add(stream);
                return stream;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
    }

    /// <inheritdoc />
    public Stream CreateNew(string relativePath)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            var (directories, leaf) = SplitFilePath(relativePath);
            if (IsOwnershipMarkerPath(relativePath))
            {
                if (!createdProductRoot)
                    throw new IOException("An ownership marker may only bootstrap a product directory created by this session.");
            }
            else
            {
                EnsureOwnedMediaRoot();
            }
            using var parent = OpenDirectoryPath(directories, createMissing: false, finalAccess: FileAddFile);
            var stream = CreateNewFile(parent, leaf);
            movableStreams.Add(stream);
            return stream;
        }
    }

    /// <inheritdoc />
    public void MoveCreatedFile(Stream createdOrRecoveryStream, string relativeDestination)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(createdOrRecoveryStream);
        EnsureOwnedMediaRoot();
        if (createdOrRecoveryStream is not FileStream sourceStream || !sourceStream.CanRead)
            throw new ArgumentException("The source must be an open file stream returned by this session.", nameof(createdOrRecoveryStream));
        lock (sync)
        {
            if (!movableStreams.Contains(createdOrRecoveryStream))
                throw new ArgumentException("The source stream was not created or opened for recovery by this session.", nameof(createdOrRecoveryStream));
            if (sourceStream.CanWrite) sourceStream.Flush(flushToDisk: true);
            var source = sourceStream.SafeFileHandle;
            EnsureExpectedVolume(source, expectedVolumeGuidPath);
            var (destinationDirectories, destinationName) = SplitFilePath(relativeDestination);
            using var destinationParent = OpenDirectoryPath(destinationDirectories, createMissing: false, finalAccess: FileAddFile);
            MoveFile(source, destinationParent, destinationName);
            movableStreams.Remove(createdOrRecoveryStream);
            sourceStream.Dispose();
        }
    }

    private static SafeFileHandle OpenDirectoryCore(SafeFileHandle parent, string name, uint disposition, string expectedVolume, uint additionalAccess = 0)
    {
        var access = FileListDirectory | FileTraverse | FileReadAttributes | Synchronize | additionalAccess;
        if (disposition is FileOpenIf or FileCreate) access |= FileAddFile | FileAddSubdirectory;
        var handle = NtOpenRelative(parent, name, access,
            FileShareRead | FileShareWrite, disposition,
            FileDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint);
        try
        {
            EnsureDirectoryNotReparsePoint(handle);
            EnsureExpectedVolume(handle, expectedVolume);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Creates a new file relative to a verified directory handle without opening or overwriting an existing name.</summary>
    /// <param name="parent">The pinned volume root or a child handle returned by this session.</param>
    /// <param name="name">One file-name component; separators and traversal components are rejected.</param>
    /// <returns>A writable stream over the newly created file handle.</returns>
    internal FileStream CreateNewFile(SafeFileHandle parent, string name)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(parent);
        ValidateComponent(name);
        EnsureExpectedVolume(parent, expectedVolumeGuidPath);
        var handle = NtOpenRelative(parent, name, GenericWrite | FileReadAttributes | Delete | Synchronize,
            FileShareRead | FileShareDelete, FileCreate, FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint);
        try
        {
            EnsureExpectedVolume(handle, expectedVolumeGuidPath);
            return new FileStream(handle, FileAccess.ReadWrite, 64 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Atomically renames an open file handle into a verified directory on this same volume.</summary>
    /// <param name="source">An open file handle created or opened beneath this session.</param>
    /// <param name="destinationDirectory">A destination directory handle returned by this session.</param>
    /// <param name="destinationName">One destination file-name component.</param>
    internal void MoveFile(SafeFileHandle source, SafeFileHandle destinationDirectory, string destinationName)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinationDirectory);
        ValidateComponent(destinationName);
        EnsureExpectedVolume(source, expectedVolumeGuidPath);
        EnsureExpectedVolume(destinationDirectory, expectedVolumeGuidPath);
        var encodedName = System.Text.Encoding.Unicode.GetBytes(destinationName);
        var fileName = new byte[encodedName.Length + sizeof(char)];
        encodedName.CopyTo(fileName, 0);
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var bufferLength = checked(nameOffset + fileName.Length + IntPtr.Size / 2);
        var buffer = IntPtr.Zero;
        var destinationReference = false;
        try
        {
            destinationDirectory.DangerousAddRef(ref destinationReference);
            buffer = Marshal.AllocHGlobal(bufferLength);
            for (var index = 0; index < bufferLength; index++) Marshal.WriteByte(buffer, index, 0);
            Marshal.WriteByte(buffer, 0, 0);
            Marshal.WriteIntPtr(buffer, rootOffset, destinationDirectory.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, encodedName.Length);
            Marshal.Copy(fileName, 0, IntPtr.Add(buffer, nameOffset), fileName.Length);
            var status = NtSetInformationFile(source, out _, buffer, checked((uint)bufferLength), FileRenameInformation);
            if (status < 0)
            {
                var error = unchecked((int)RtlNtStatusToDosError(status));
                throw new Win32Exception(error, $"The handle-relative media file rename failed (NTSTATUS 0x{status:X8}, Win32 {error}).");
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (destinationReference) destinationDirectory.DangerousRelease();
        }
    }

    /// <summary>Returns a duplicate handle to the pinned volume root for safe relative child operations.</summary>
    internal SafeFileHandle DuplicateRootHandle()
    {
        lock (sync)
        {
            ThrowIfDisposed();
            return DuplicateHandleCore(volumeRoot, "The volume root handle could not be duplicated.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            foreach (var stream in movableStreams) stream.Dispose();
            movableStreams.Clear();
            productRootHandle?.Dispose();
            productRootHandle = null;
            foreach (var directory in pinnedDirectories.Values) directory.Dispose();
            pinnedDirectories.Clear();
            volumeRoot.Dispose();
        }
    }

    private static SafeFileHandle NtOpenRelative(SafeFileHandle parent, string name, uint desiredAccess, uint shareAccess, uint disposition, uint options)
    {
        var parentReference = false;
        var nameBuffer = IntPtr.Zero;
        var unicodeNamePointer = IntPtr.Zero;
        var attributesPointer = IntPtr.Zero;
        try
        {
        parent.DangerousAddRef(ref parentReference);
        nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeName = new UnicodeString
        {
            Length = checked((ushort)(name.Length * sizeof(char))),
            MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
            Buffer = nameBuffer
        };
        unicodeNamePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var attributes = new ObjectAttributes
        {
            Length = Marshal.SizeOf<ObjectAttributes>(),
            RootDirectory = parent.DangerousGetHandle(),
            ObjectName = unicodeNamePointer,
            Attributes = ObjectCaseInsensitive
        };
        attributesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<ObjectAttributes>());
            Marshal.StructureToPtr(unicodeName, unicodeNamePointer, false);
            Marshal.StructureToPtr(attributes, attributesPointer, false);
            var status = NtCreateFile(out var rawHandle, desiredAccess, attributesPointer, out _, IntPtr.Zero,
                0x00000080, shareAccess, disposition, options, IntPtr.Zero, 0);
            if (status < 0)
            {
                var error = unchecked((int)RtlNtStatusToDosError(status));
                if (error == 2) throw new FileNotFoundException($"The handle-relative media entry was not found (NTSTATUS 0x{status:X8}).", name);
                if (error == 3) throw new DirectoryNotFoundException($"The handle-relative media directory was not found (NTSTATUS 0x{status:X8}).");
                throw new Win32Exception(error, $"The handle-relative media path operation failed (NTSTATUS 0x{status:X8}).");
            }
            return new SafeFileHandle(rawHandle, ownsHandle: true);
        }
        finally
        {
            if (attributesPointer != IntPtr.Zero) Marshal.FreeHGlobal(attributesPointer);
            if (unicodeNamePointer != IntPtr.Zero) Marshal.FreeHGlobal(unicodeNamePointer);
            if (nameBuffer != IntPtr.Zero) Marshal.FreeHGlobal(nameBuffer);
            if (parentReference) parent.DangerousRelease();
        }
    }

    private static void EnsureExpectedVolume(SafeFileHandle handle, string expected)
    {
        if (handle.IsInvalid || handle.IsClosed) throw new IOException("The media handle is invalid or closed.");
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0x1);
        if (length == 0 || length >= buffer.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "The handle's volume GUID could not be determined.");
        var finalPath = new string(buffer, 0, checked((int)length));
        var actualVolume = NormalizeVolumeGuidPath(finalPath[..(finalPath.IndexOf('}', StringComparison.Ordinal) + 1)] + "\\");
        if (!string.Equals(actualVolume, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The opened media handle is not on the expected volume GUID.");
        }
    }

    private static SafeFileHandle DuplicateHandleToSelf(SafeFileHandle source)
    {
        if (!DuplicateHandle(GetCurrentProcess(), source, GetCurrentProcess(), out var duplicate, 0, false, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The volume directory handle could not be duplicated.");
        return duplicate;
    }

    private static void EnsureDirectoryNotReparsePoint(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out var info, (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The directory handle attributes could not be verified.");
        }

        if ((info.FileAttributes & FileAttributeReparsePoint) != 0) throw new IOException("Media storage directories may not be reparse points.");
    }

    private static string NormalizeVolumeGuidPath(string path)
    {
        var value = path.Trim();
        if (!value.StartsWith("\\\\?\\Volume{", StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(value.AsSpan(11, Math.Max(0, value.IndexOf('}') - 11)), out _))
        {
            throw new ArgumentException("A valid Windows volume GUID path is required.", nameof(path));
        }

        var closingBrace = value.IndexOf('}');
        if (closingBrace < 0 || value[(closingBrace + 1)..].Trim('\\').Length != 0) throw new ArgumentException("The path must name the volume root, not a child path.", nameof(path));
        return value[..(closingBrace + 1)] + "\\";
    }

    private static void ValidateComponent(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value is "." or ".." || value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':', '\0']) >= 0)
            throw new ArgumentException("Only one non-traversal path component is allowed.", nameof(value));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private static IReadOnlyList<string> SplitRelativePath(string relativePath, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            if (allowEmpty) return Array.Empty<string>();
            throw new ArgumentException("A non-empty volume-relative path is required.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("Only volume-relative paths are accepted.", nameof(relativePath));
        var components = relativePath.Split(['\\', '/'], StringSplitOptions.None);
        foreach (var component in components) ValidateComponent(component);
        if (components.Length == 0 || !components[0].Equals(ProductDirectoryName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Media filesystem operations are confined to the product-owned media directory.", nameof(relativePath));
        return components;
    }

    private static (IReadOnlyList<string> Directories, string Leaf) SplitFilePath(string relativePath)
    {
        var components = SplitRelativePath(relativePath, allowEmpty: false);
        return (components.Take(components.Count - 1).ToArray(), components[^1]);
    }

    private static bool IsProductRootPath(string relativePath) => relativePath.TrimEnd('\\', '/').Equals(ProductDirectoryName, StringComparison.OrdinalIgnoreCase);

    private static bool IsOwnershipMarkerPath(string relativePath) => relativePath.Replace('/', '\\').Equals(ProductDirectoryName + "\\" + OwnershipMarkerName, StringComparison.OrdinalIgnoreCase);

    private void EnsureOwnedMediaRoot()
    {
        using var productRoot = OpenDirectoryPath([ProductDirectoryName], createMissing: false);
        EnsureOwnedProductRoot(productRoot, expectedVolumeGuidPath);
        lock (sync)
        {
            ThrowIfDisposed();
            productRootHandle ??= OpenDirectoryCore(volumeRoot, ProductDirectoryName, FileOpen, expectedVolumeGuidPath, FileAddFile | FileAddSubdirectory);
        }
    }

    private SafeFileHandle OpenDirectoryPath(IReadOnlyList<string> components, bool createMissing, uint finalAccess = 0)
    {
        var firstComponent = 0;
        SafeFileHandle current;
        if (components.Count > 0 && components[0].Equals(ProductDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            lock (sync)
            {
                ThrowIfDisposed();
                if (productRootHandle is not null)
                {
                    current = DuplicateHandleCore(productRootHandle, "The pinned product directory handle could not be duplicated.");
                    firstComponent = 1;
                }
                else
                {
                    current = DuplicateHandleCore(volumeRoot, "The volume root handle could not be duplicated.");
                }
            }
        }
        else
        {
            current = components.Count == 0 && finalAccess != 0 ? OpenVolumeRootWithAccess(finalAccess) : DuplicateRootHandle();
        }
        try
        {
        for (var index = firstComponent; index < components.Count; index++)
        {
            var component = components[index];
                var isPinnedProductPath = firstComponent == 1;
                var cacheKey = isPinnedProductPath ? string.Join('\\', components.Skip(1).Take(index)) : string.Empty;
                SafeFileHandle? cachedHandle = null;
                if (isPinnedProductPath)
                {
                    lock (sync)
                    {
                        ThrowIfDisposed();
                        if (pinnedDirectories.TryGetValue(cacheKey, out var cached))
                            cachedHandle = DuplicateHandleCore(cached, "A pinned media directory handle could not be duplicated.");
                    }
                }
                var next = cachedHandle ?? (createMissing ? OpenOrCreateDirectory(current, component) : OpenDirectory(current, component));
                if (index == components.Count - 1 && finalAccess != 0 && cachedHandle is not null)
                {
                    next.Dispose();
                    next = OpenDirectoryCore(current, component, FileOpen, expectedVolumeGuidPath, finalAccess);
                }
                if (isPinnedProductPath)
                {
                    lock (sync)
                    {
                        ThrowIfDisposed();
                        if (pinnedDirectories.TryGetValue(cacheKey, out var previous))
                        {
                            if (cachedHandle is null || (index == components.Count - 1 && finalAccess != 0))
                            {
                                pinnedDirectories[cacheKey] = DuplicateHandleCore(next, "A media directory handle could not be pinned.");
                                previous.Dispose();
                            }
                        }
                        else
                        {
                            pinnedDirectories.Add(cacheKey, DuplicateHandleCore(next, "A media directory handle could not be pinned."));
                        }
                    }
                }
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle DuplicateHandleCore(SafeFileHandle source, string errorMessage)
    {
        if (!DuplicateHandle(GetCurrentProcess(), source, GetCurrentProcess(), out var duplicate, 0, false, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error(), errorMessage);
        return duplicate;
    }

    private SafeFileHandle OpenVolumeRootWithAccess(uint access)
    {
        if (rootHasCreateAccess) return DuplicateRootHandle();
        lock (sync)
        {
            ThrowIfDisposed();
            var handle = CreateFile(expectedVolumeGuidPath, FileReadAttributes | FileListDirectory | FileTraverse | access | Synchronize,
                FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "The volume root could not be reopened with the required product-directory access.");
            }

            try
            {
                EnsureExpectedVolume(handle, expectedVolumeGuidPath);
                EnsureDirectoryNotReparsePoint(handle);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
    }

    private SafeFileHandle OpenEntryHandle(SafeFileHandle parent, string name, uint desiredAccess = FileReadAttributes | Synchronize, uint shareAccess = FileShareRead | FileShareWrite | FileShareDelete)
    {
        ValidateComponent(name);
        EnsureExpectedVolume(parent, expectedVolumeGuidPath);
        var handle = NtOpenRelative(parent, name, desiredAccess, shareAccess,
            FileOpen, FileOpenReparsePoint | FileSynchronousIoNonAlert);
        try
        {
            if ((GetAttributes(handle) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Media storage paths may not traverse reparse points.");
            EnsureExpectedVolume(handle, expectedVolumeGuidPath);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static FileAttributes GetAttributes(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out var info,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The media entry attributes could not be verified.");
        return (FileAttributes)info.FileAttributes;
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0x1);
        if (length == 0 || length >= buffer.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The media directory path could not be resolved.");
        return new string(buffer, 0, checked((int)length));
    }

    private static IEnumerable<string> EnumerateNames(SafeFileHandle directory)
    {
        const int nameOffset = 12;
        const int bufferSize = 64 * 1024;
        var buffer = new byte[bufferSize];
        var restart = (byte)1;
        while (true)
        {
            Array.Clear(buffer);
            var status = NtQueryDirectoryFile(directory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var ioStatus,
                buffer, bufferSize, FileNamesInformation, returnSingleEntry: 0, IntPtr.Zero, restart);
            restart = 0;
            if (status == StatusNoMoreFiles) yield break;
            if (status < 0 && status != unchecked((int)0x80000005))
                throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)), $"Handle-relative media directory enumeration failed (NTSTATUS 0x{status:X8}).");

            var limit = checked((int)Math.Min((ulong)buffer.Length, ioStatus.Information.ToUInt64()));
            var offset = 0;
            while (offset + nameOffset <= limit)
            {
                var nextOffset = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, sizeof(int)));
                var nameLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset + 8, sizeof(int)));
                if (nameLength < 0 || (nameLength & 1) != 0 || nameLength > limit - offset - nameOffset)
                    throw new InvalidDataException("The native directory enumeration returned an invalid name length.");
                yield return System.Text.Encoding.Unicode.GetString(buffer, offset + nameOffset, nameLength);
                if (nextOffset == 0) break;
                if (nextOffset < nameOffset || nextOffset > limit - offset)
                    throw new InvalidDataException("The native directory enumeration returned an invalid entry offset.");
                offset += nextOffset;
            }

            if (ioStatus.Information == UIntPtr.Zero) yield break;
        }
    }

    private static void EnsureOwnedProductRoot(SafeFileHandle productRoot, string expectedVolume)
    {
        if (!TryOpenEntryHandle(productRoot, OwnershipMarkerName, expectedVolume, out var markerHandle, FileReadData | FileReadAttributes | Synchronize))
            throw new IOException("The existing media directory has no valid Storage Chronicle ownership marker and will not be modified.");

        using (markerHandle)
        {
            EnsureExpectedVolume(markerHandle, expectedVolume);
            if ((GetAttributes(markerHandle) & FileAttributes.ReparsePoint) != 0 || (GetAttributes(markerHandle) & FileAttributes.Directory) != 0)
                throw new IOException("The media ownership marker is not a regular file.");
            using var input = new FileStream(markerHandle, FileAccess.Read, 4096, isAsync: false);
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (!root.TryGetProperty("schema", out var schema) || schema.GetString() != OwnershipSchema ||
                !root.TryGetProperty("writerId", out var writer) || writer.ValueKind != JsonValueKind.Null)
                throw new IOException("The existing media ownership marker is invalid or incompatible.");
        }
    }

    private static bool TryOpenEntryHandle(SafeFileHandle parent, string name, string expectedVolume, out SafeFileHandle handle, uint desiredAccess = FileReadAttributes | Synchronize)
    {
        SafeFileHandle? opened = null;
        try
        {
            opened = NtOpenRelative(parent, name, desiredAccess, FileShareRead | FileShareWrite | FileShareDelete,
                FileOpen, FileOpenReparsePoint | FileSynchronousIoNonAlert);
            if ((GetAttributes(opened) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Media storage paths may not traverse reparse points.");
            EnsureExpectedVolume(opened, expectedVolume);
            handle = opened;
            return true;
        }
        catch (FileNotFoundException)
        {
            opened?.Dispose();
            handle = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
            return false;
        }
        catch
        {
            opened?.Dispose();
            throw;
        }
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
    private struct FileAttributeTagInformation { public uint FileAttributes; public uint ReparseTag; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] path, uint pathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileAttributeTagInformation info, uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle sourceHandle, IntPtr targetProcess, out SafeFileHandle targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out IntPtr handle, uint desiredAccess, IntPtr objectAttributes, out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryFile(SafeFileHandle fileHandle, IntPtr eventHandle, IntPtr apcRoutine, IntPtr apcContext, out IoStatusBlock ioStatusBlock, [Out] byte[] fileInformation, uint length, int fileInformationClass, byte returnSingleEntry, IntPtr fileName, byte restartScan);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock ioStatusBlock, IntPtr fileInformation, uint length, int informationClass);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }
}
