namespace StorageChronicle.UI.DiffView;

/// <summary>A lexical breadcrumb for a path shown by Diff View.</summary>
public sealed record DiffPathBreadcrumb(string Label, string Path, int Index);

/// <summary>Builds and validates path navigation lexically without querying a file system.</summary>
public static class DiffPathNavigation
{
    /// <summary>Returns whether a path is rooted on either supported path syntax.</summary>
    public static bool IsRooted(string? path) => !string.IsNullOrWhiteSpace(path) &&
        (path[0] is '/' or '\\' || path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\');

    /// <summary>Builds root-to-leaf breadcrumbs for a rooted path, or a single breadcrumb for a virtual label.</summary>
    public static IReadOnlyList<DiffPathBreadcrumb> BuildBreadcrumbs(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<DiffPathBreadcrumb>();
        if (!IsRooted(path)) return [new DiffPathBreadcrumb(path, path, 0)];

        var separator = path.Contains('\\') && !path.Contains('/') ? '\\' : '/';
        var rooted = path.Replace('\\', '/');
        var drive = rooted.Length >= 2 && rooted[1] == ':' ? rooted[..2] : string.Empty;
        var segments = rooted[(drive.Length > 0 ? 2 : 0)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var paths = new List<(string Label, string Path)>();
        if (drive.Length == 0 && rooted.StartsWith("//", StringComparison.Ordinal) && segments.Length >= 2)
        {
            var shareRoot = new string(separator, 2) + segments[0] + separator + segments[1] + separator;
            paths.Add((shareRoot, shareRoot));
            var currentSharePath = shareRoot;
            foreach (var segment in segments.Skip(2))
            {
                currentSharePath += segment + separator;
                paths.Add((segment, currentSharePath));
            }
            return paths.Select((entry, index) => new DiffPathBreadcrumb(entry.Label, entry.Path, index)).ToArray();
        }
        var current = drive.Length > 0 ? drive + separator : separator.ToString();
        paths.Add((drive.Length > 0 ? drive + separator : separator.ToString(), current));
        foreach (var segment in segments)
        {
            current = current.EndsWith(separator) ? current + segment : current + separator + segment;
            paths.Add((segment, current));
        }
        return paths.Select((entry, index) => new DiffPathBreadcrumb(entry.Label, entry.Path, index)).ToArray();
    }

    /// <summary>Returns the lexical parent of a rooted path, retaining drive and filesystem roots.</summary>
    public static string? GetParent(string? path)
    {
        if (!IsRooted(path)) return null;
        var separator = path!.Contains('\\') && !path.Contains('/') ? '\\' : '/';
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            var uncSegments = normalized[2..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (uncSegments.Length <= 2) return null;
            var uncRoot = new string(separator, 2) + uncSegments[0] + separator + uncSegments[1];
            var currentUncPath = path.TrimEnd('/', '\\');
            var parentSeparator = currentUncPath.LastIndexOfAny(['/', '\\']);
            return parentSeparator <= uncRoot.Length ? uncRoot + separator : currentUncPath[..parentSeparator];
        }
        var rootLength = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\' ? 3 : 1;
        var trimmed = path.TrimEnd('/', '\\');
        if (trimmed.Length < rootLength) return null;
        var split = trimmed.LastIndexOfAny(['/', '\\']);
        if (split < rootLength) return path[..rootLength];
        if (split == 0) return path[..1];
        return trimmed[..split];
    }

    /// <summary>Validates and normalizes a directly entered path without accessing the host file system.</summary>
    public static bool TryNormalizeDirectPath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !IsRooted(path)) return false;
        var candidate = path.Trim();
        if (candidate.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or "..")) return false;
        var rootLength = candidate.Length >= 3 && char.IsAsciiLetter(candidate[0]) && candidate[1] == ':' && candidate[2] is '/' or '\\' ? 3 : 1;
        normalized = candidate.Length > rootLength ? candidate.TrimEnd('/', '\\') : candidate;
        return true;
    }
}

/// <summary>State for one side of the optional split Diff View.</summary>
public sealed class DiffPaneState
{
    /// <summary>Initializes a pane with its stable side name.</summary>
    public DiffPaneState(string name) => Name = name;
    /// <summary>Gets the pane name.</summary>
    public string Name { get; }
    /// <summary>Gets or sets the path shown in the pane.</summary>
    public string? CurrentPath { get; internal set; }
    /// <summary>Gets or sets whether the pane is pinned to its selection.</summary>
    public bool IsPinned { get; set; }
}

/// <summary>Tracks split-pane state without creating a second projection.</summary>
public sealed class DiffSplitPaneState
{
    /// <summary>Left pane state.</summary>
    public DiffPaneState Left { get; } = new("left");
    /// <summary>Right pane state.</summary>
    public DiffPaneState Right { get; } = new("right");
    /// <summary>Gets whether two panes are visible.</summary>
    public bool IsSplit { get; private set; }
    /// <summary>Gets the left-pane ratio.</summary>
    public double SplitRatio { get; private set; } = 0.5;

    /// <summary>Enables or disables split panes.</summary>
    public void SetSplit(bool split) => IsSplit = split;

    /// <summary>Sets the ratio while retaining a usable minimum for both panes.</summary>
    public void SetRatio(double ratio)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio is < 0.1 or > 0.9) throw new ArgumentOutOfRangeException(nameof(ratio));
        SplitRatio = ratio;
    }
}

/// <summary>Replay cursor and speed state for the selected file timeline.</summary>
public sealed class DiffReplayState
{
    /// <summary>Supported replay rates, expressed as real-time multiples.</summary>
    public static IReadOnlyList<double> SupportedSpeeds { get; } = Array.AsReadOnly(new[] { 1d, 2d, 10d });
    /// <summary>Gets or sets the current replay cursor.</summary>
    public DateTimeOffset? CursorUtc { get; internal set; }
    /// <summary>Gets or sets replay speed in multiples of real time.</summary>
    public double Speed { get; private set; } = 1;
    /// <summary>Gets whether replay is currently playing.</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>Sets replay speed to exactly 1x, 2x, or 10x.</summary>
    public void SetSpeed(double speed)
    {
        if (!SupportedSpeeds.Contains(speed)) throw new ArgumentOutOfRangeException(nameof(speed), "Replay speed must be one of 1x, 2x, or 10x.");
        Speed = speed;
    }

    /// <summary>Starts or resumes replay playback state.</summary>
    public void Play() => IsPlaying = true;
    /// <summary>Pauses replay playback state.</summary>
    public void Pause() => IsPlaying = false;
    /// <summary>Gets whether the replay cursor is at the current projection endpoint.</summary>
    public bool IsAtPresent { get; internal set; }
}

