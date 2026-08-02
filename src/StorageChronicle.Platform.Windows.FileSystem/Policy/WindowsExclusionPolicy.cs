using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.FileSystem.Policy;

/// <summary>Applies Storage Chronicle, Windows standard, and user exclusion rules before event creation.</summary>
public sealed class WindowsExclusionPolicy
{
    private static readonly string[] StandardDirectoryNames = ["System Volume Information", "$Recycle.Bin", "$RECYCLE.BIN"];
    private readonly string? storageChronicleRoot;
    private readonly string[] userRoots;

    /// <summary>Initializes an exclusion policy.</summary>
    public WindowsExclusionPolicy(WindowsFileSystemOptions? options = null)
    {
        options ??= new WindowsFileSystemOptions();
        storageChronicleRoot = NormalizeRoot(options.StorageChronicleDataRoot);
        userRoots = options.UserExcludedRoots.Select(NormalizeRoot).Where(static value => value is not null).Cast<string>().ToArray();
    }

    /// <summary>Returns whether a path is excluded before metadata or events are created.</summary>
    public bool ShouldExclude(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var normalized = NormalizeRoot(fullPath) ?? fullPath;
        if (storageChronicleRoot is not null && IsSameOrDescendant(normalized, storageChronicleRoot))
        {
            return true;
        }

        if (userRoots.Any(root => IsSameOrDescendant(normalized, root)))
        {
            return true;
        }

        var name = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar));
        return StandardDirectoryNames.Any(standard => string.Equals(name, standard, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns whether a path is a Windows reparse point that must not be traversed.</summary>
    public static bool IsReparsePoint(FileAttributes attributes) => (attributes & FileAttributes.ReparsePoint) != 0;

    private static string? NormalizeRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Length == 0 ? Path.DirectorySeparatorChar.ToString() : fullPath;
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
