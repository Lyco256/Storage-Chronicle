namespace StorageChronicle.UI.DiffView;

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
    /// <summary>Gets or sets the current replay cursor.</summary>
    public DateTimeOffset? CursorUtc { get; internal set; }
    /// <summary>Gets or sets replay speed in multiples of real time.</summary>
    public double Speed { get; private set; } = 1;
    /// <summary>Gets whether replay is currently playing.</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>Sets a bounded replay speed.</summary>
    public void SetSpeed(double speed)
    {
        if (double.IsNaN(speed) || double.IsInfinity(speed) || speed is < 0.25 or > 16) throw new ArgumentOutOfRangeException(nameof(speed));
        Speed = speed;
    }

    /// <summary>Starts replay playback state.</summary>
    public void Play() => IsPlaying = true;
    /// <summary>Pauses replay playback state.</summary>
    public void Pause() => IsPlaying = false;
}

