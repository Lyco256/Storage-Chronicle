using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.FileSystem.Policy;

/// <summary>Applies Storage Chronicle, Windows standard, and user exclusion rules before event creation.</summary>
public sealed class WindowsExclusionPolicy
{
    private static readonly string[] StandardDirectoryNames = ["System Volume Information", "$Recycle.Bin", "$RECYCLE.BIN"];
    private readonly string? storageChronicleRoot;
    private readonly object gate = new();
    private string[] userRoots;
    private string[] monitoredRoots;
    private readonly HashSet<string> dynamicRoots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes an exclusion policy.</summary>
    public WindowsExclusionPolicy(WindowsFileSystemOptions? options = null)
    {
        options ??= new WindowsFileSystemOptions();
        storageChronicleRoot = NormalizeRoot(options.StorageChronicleDataRoot);
        userRoots = options.UserExcludedRoots.Select(NormalizeRoot).Where(static value => value is not null).Cast<string>().ToArray();
        monitoredRoots = options.MonitoredRoots.Select(NormalizeRoot).Where(static value => value is not null).Cast<string>().ToArray();
    }

    /// <summary>Returns whether a path is excluded before metadata or events are created.</summary>
    public bool ShouldExclude(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var normalized = NormalizeRoot(fullPath) ?? fullPath;
        string[] configuredRoots;
        lock (gate) configuredRoots = userRoots.Concat(dynamicRoots).ToArray();
        if (storageChronicleRoot is not null && IsSameOrDescendant(normalized, storageChronicleRoot))
        {
            return true;
        }

        if (configuredRoots.Any(root => IsSameOrDescendant(normalized, root)))
        {
            return true;
        }

        var name = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar));
        return StandardDirectoryNames.Any(standard => string.Equals(name, standard, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns whether a path is a Windows reparse point that must not be traversed.</summary>
    public static bool IsReparsePoint(FileAttributes attributes) => (attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>Registers a runtime exclusion such as an external-media log folder.</summary>
    public string RegisterDynamicRoot(string path)
    {
        var normalized = NormalizeRoot(path) ?? throw new ArgumentException("An exclusion root is required.", nameof(path));
        lock (gate) dynamicRoots.Add(normalized);
        return normalized;
    }

    /// <summary>Replaces user-configured exclusion roots after a validated settings update.</summary>
    public void SetUserExcludedRoots(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var normalized = roots.Select(NormalizeRoot).Where(static value => value is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (gate) userRoots = normalized;
    }

    /// <summary>Replaces the validated monitoring roots used to scope volume collection.</summary>
    public void SetMonitoredRoots(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var normalized = roots.Select(NormalizeRoot).Where(static value => value is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (gate) monitoredRoots = normalized;
    }

    /// <summary>Returns whether a volume root intersects the configured monitoring scope.</summary>
    public bool IsMonitoredRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = NormalizeRoot(path) ?? path;
        lock (gate)
        {
            return monitoredRoots.Length == 0 || monitoredRoots.Any(root => IsSameOrDescendant(normalized, root) || IsSameOrDescendant(root, normalized));
        }
    }

    /// <summary>Returns whether a volume is monitored in full and can use its higher-fidelity USN collector.</summary>
    public bool IsWholeVolumeMonitored(string volumeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        var normalized = NormalizeRoot(volumeRoot) ?? volumeRoot;
        lock (gate)
        {
            return monitoredRoots.Length == 0 || monitoredRoots.Any(root => string.Equals(root, normalized, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Resolves the deepest configured monitoring root intersecting a volume root.</summary>
    public string? ResolveMonitoringRoot(string volumeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        var normalizedVolume = NormalizeRoot(volumeRoot) ?? volumeRoot;
        lock (gate)
        {
            if (monitoredRoots.Length == 0) return normalizedVolume;
            return monitoredRoots.Where(root => IsSameOrDescendant(root, normalizedVolume) || IsSameOrDescendant(normalizedVolume, root)).OrderByDescending(root => root.Length).FirstOrDefault();
        }
    }

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
