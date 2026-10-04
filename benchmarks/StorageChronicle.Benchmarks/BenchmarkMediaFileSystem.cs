using System.Runtime.InteropServices;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
#if WINDOWS
using StorageChronicle.Platform.Windows.FileSystem.Interop;
#endif

namespace StorageChronicle.Benchmarks;

internal static class BenchmarkMediaFileSystem
{
    public static (VolumeId VolumeId, IVolumeBoundMediaFileSystem FileSystem) Open(string root)
    {
        var fullRoot = Path.GetFullPath(root);
#if WINDOWS
        var mountPoint = new char[1024];
        if (!GetVolumePathName(fullRoot, mountPoint, (uint)mountPoint.Length)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var volumeGuid = new char[1024];
        var mount = new string(mountPoint).TrimEnd('\0');
        if (!GetVolumeNameForVolumeMountPoint(mount, volumeGuid, (uint)volumeGuid.Length)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var session = WindowsVolumeDirectorySession.OpenAtExistingDirectory(new string(volumeGuid).TrimEnd('\0'), mount, fullRoot);
        return (session.VolumeId, session);
#else
        Directory.CreateDirectory(fullRoot);
        var volumeId = VolumeId.Create(fullRoot);
        return (volumeId, new PortableFixtureMediaFileSystem(fullRoot, volumeId));
#endif
    }

#if WINDOWS
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumePathNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, [Out] char[] volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, [Out] char[] volumeName, uint bufferLength);
#endif

    private sealed class PortableFixtureMediaFileSystem(string root, VolumeId volumeId) : IVolumeBoundMediaFileSystem
    {
        private readonly HashSet<Stream> movable = new(ReferenceEqualityComparer.Instance);
        public VolumeId VolumeId { get; } = volumeId;
        public string GetOwnedProductDirectoryIdentity() => throw new NotSupportedException("A benchmark path fixture cannot provide authoritative Windows file identity evidence.");
        public void EnsureDirectory(string relativePath) => Directory.CreateDirectory(Resolve(relativePath));

        public bool TryCreateDirectory(string relativePath)
        {
            var path = Resolve(relativePath);
            if (Directory.Exists(path) || File.Exists(path)) return false;
            Directory.CreateDirectory(path);
            return true;
        }

        public bool DirectoryExists(string relativePath) => Directory.Exists(Resolve(relativePath));
        public bool FileExists(string relativePath) => File.Exists(Resolve(relativePath));
        public IReadOnlyList<MediaFileSystemEntry> EnumerateEntries(string relativeDirectory) => Directory.EnumerateFileSystemEntries(Resolve(relativeDirectory))
            .Select(path => new MediaFileSystemEntry(Path.GetFileName(path), File.GetAttributes(path))).ToArray();
        public Stream OpenRead(string relativePath) => new FileStream(Resolve(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);

        public Stream OpenTemporaryForRecovery(string relativePath)
        {
            var path = Resolve(relativePath);
            var leaf = Path.GetFileName(path);
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(leaf), "N", out _) || !leaf.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only GUID-named segment temporaries are recoverable.", nameof(relativePath));
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            movable.Add(stream);
            return stream;
        }

        public Stream CreateNew(string relativePath)
        {
            var stream = new FileStream(Resolve(relativePath), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            movable.Add(stream);
            return stream;
        }

        public void MoveCreatedFile(Stream createdOrRecoveryStream, string relativeDestination)
        {
            if (createdOrRecoveryStream is not FileStream fileStream || !movable.Remove(createdOrRecoveryStream))
                throw new ArgumentException("The source was not created or opened by this benchmark fixture.", nameof(createdOrRecoveryStream));
            File.Move(fileStream.Name, Resolve(relativeDestination));
        }

        public void Dispose() { }

        private string Resolve(string relativePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            var components = relativePath.Split(['\\', '/'], StringSplitOptions.None);
            if (Path.IsPathRooted(relativePath) || components.Any(component => component is "" or "." or ".." || component.Contains(':')))
                throw new ArgumentException("A benchmark media path must be relative.", nameof(relativePath));
            var path = Path.GetFullPath(Path.Combine(root, Path.Combine(components)));
            var boundary = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(boundary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ArgumentException("The benchmark path escaped its unique temporary root.", nameof(relativePath));
            return path;
        }
    }
}
