using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Agent.Tests;

internal sealed class FixtureVolumeFileSystemFactory(string root) : IVolumeBoundMediaFileSystemFactory
{
    private readonly string root = Path.GetFullPath(root);

    public IVolumeBoundMediaFileSystem Open(VolumeId expectedVolumeId) => new FixtureVolumeFileSystem(root, expectedVolumeId);

    private sealed class FixtureVolumeFileSystem(string root, VolumeId volumeId) : IVolumeBoundMediaFileSystem
    {
        private readonly HashSet<Stream> movableStreams = new(ReferenceEqualityComparer.Instance);
        public VolumeId VolumeId { get; } = volumeId;
        public string GetOwnedProductDirectoryIdentity() => throw new NotSupportedException("A path-based test fixture cannot provide authoritative Windows file identity evidence.");
        public void EnsureDirectory(string relativePath) => Directory.CreateDirectory(Resolve(relativePath));

        public bool TryCreateDirectory(string relativePath)
        {
            var path = Resolve(relativePath);
            if (Directory.Exists(path) || File.Exists(path)) return false;
            Directory.CreateDirectory(path);
            return true;
        }

        public bool DirectoryExists(string relativePath)
        {
            var path = Resolve(relativePath);
            return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }

        public bool FileExists(string relativePath)
        {
            var path = Resolve(relativePath);
            return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }

        public IReadOnlyList<MediaFileSystemEntry> EnumerateEntries(string relativeDirectory) => Directory.EnumerateFileSystemEntries(Resolve(relativeDirectory))
            .Select(path => new MediaFileSystemEntry(Path.GetFileName(path), File.GetAttributes(path))).ToArray();

        public Stream OpenRead(string relativePath)
        {
            var path = Resolve(relativePath);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture path may not be a reparse point.");
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        public Stream OpenTemporaryForRecovery(string relativePath)
        {
            var path = Resolve(relativePath);
            var leaf = Path.GetFileName(path);
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(leaf), "N", out _) || !leaf.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only GUID-named temporary fixture files are recoverable.", nameof(relativePath));
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            movableStreams.Add(stream);
            return stream;
        }

        public Stream CreateNew(string relativePath)
        {
            var stream = new FileStream(Resolve(relativePath), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            movableStreams.Add(stream);
            return stream;
        }

        public void MoveCreatedFile(Stream createdOrRecoveryStream, string relativeDestination)
        {
            if (createdOrRecoveryStream is not FileStream fileStream || !movableStreams.Remove(createdOrRecoveryStream))
                throw new ArgumentException("The source stream was not created or opened for recovery by this fixture.", nameof(createdOrRecoveryStream));
            File.Move(fileStream.Name, Resolve(relativeDestination));
        }
        public void Dispose() { }

        private string Resolve(string relativePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            var components = relativePath.Split(['\\', '/'], StringSplitOptions.None);
            if (Path.IsPathRooted(relativePath) || components.Any(value => value is "" or "." or ".." || value.Contains(':')))
                throw new ArgumentException("Fixture paths must be safe relative paths.", nameof(relativePath));
            var path = Path.GetFullPath(Path.Combine(root, Path.Combine(components)));
            var boundary = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(boundary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ArgumentException("Fixture path escaped the isolated root.", nameof(relativePath));
            return path;
        }
    }
}
