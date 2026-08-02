namespace StorageChronicle.Settings;

/// <summary>Identifies the machine noise filtering policy.</summary>
public enum NoiseFilterProfile
{
    /// <summary>The standard balanced filter.</summary>
    Standard,
    /// <summary>A more selective filter for high-volume sources.</summary>
    Aggressive
}

/// <summary>Defines the ordering of event-stack rows.</summary>
public enum EventStackSortOrder
{
    /// <summary>Newest events appear first.</summary>
    NewestFirst,
    /// <summary>Oldest events appear first.</summary>
    OldestFirst
}

/// <summary>Defines the initial event-stack grouping mode.</summary>
public enum EventStackInitialMode
{
    /// <summary>Show the chronological event stream.</summary>
    Timeline,
    /// <summary>Group events by activity.</summary>
    ActivityGroup,
    /// <summary>Group events by file.</summary>
    File
}

/// <summary>Defines the representation used by Diff View.</summary>
public enum DiffDisplayFormat
{
    /// <summary>Show before and after values in one row.</summary>
    Unified,
    /// <summary>Show before and after values in separate columns.</summary>
    SideBySide
}

/// <summary>Defines the order of Diff View columns.</summary>
public enum DiffColumnOrder
{
    /// <summary>Show the before column before the after column.</summary>
    BeforeAfter,
    /// <summary>Show the after column before the before column.</summary>
    AfterBefore
}

/// <summary>Contains settings owned by the machine-wide monitoring agent.</summary>
public sealed record MachineSettings
{
    /// <summary>Gets the paths monitored by the agent.</summary>
    public IReadOnlyList<string> MonitoringPaths { get; init; } = Array.Empty<string>();
    /// <summary>Gets paths excluded from monitoring.</summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = Array.Empty<string>();
    /// <summary>Gets the standard noise-filter profile.</summary>
    public NoiseFilterProfile NoiseFilter { get; init; } = NoiseFilterProfile.Standard;
    /// <summary>Gets the directory used for durable logs.</summary>
    public string LogStoragePath { get; init; } = string.Empty;
    /// <summary>Gets the mirror target for each media identifier.</summary>
    public IReadOnlyDictionary<string, string> MediaMirrors { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Gets the event flush interval in seconds.</summary>
    public int FlushIntervalSeconds { get; init; } = 5;
}

/// <summary>Contains settings owned by the current user.</summary>
public sealed record UserSettings
{
    /// <summary>Gets the activity-group timeout in seconds.</summary>
    public double ActivityGroupTimeoutSeconds { get; init; } = 2;
    /// <summary>Gets the pane timeout in seconds.</summary>
    public double PaneTimeoutSeconds { get; init; } = 5;
    /// <summary>Gets the number of rows in one Event Stack page.</summary>
    public int EventStackPageSize { get; init; } = 250;
    /// <summary>Gets the Event Stack row ordering.</summary>
    public EventStackSortOrder EventStackSort { get; init; } = EventStackSortOrder.NewestFirst;
    /// <summary>Gets the mode initially selected in Event Stack.</summary>
    public EventStackInitialMode InitialEventStackMode { get; init; } = EventStackInitialMode.Timeline;
    /// <summary>Gets the Diff View display format.</summary>
    public DiffDisplayFormat DiffFormat { get; init; } = DiffDisplayFormat.SideBySide;
    /// <summary>Gets the Diff View zoom percentage.</summary>
    public int DiffZoomPercent { get; init; } = 100;
    /// <summary>Gets the Diff View column ordering.</summary>
    public DiffColumnOrder DiffColumns { get; init; } = DiffColumnOrder.BeforeAfter;
    /// <summary>Gets saved user filters.</summary>
    public IReadOnlyList<string> SavedFilters { get; init; } = Array.Empty<string>();
}

/// <summary>Provides the product defaults for both settings scopes.</summary>
public static class DefaultSettings
{
    /// <summary>Creates machine defaults with the supplied platform paths.</summary>
    public static MachineSettings CreateMachine(string monitoringPath, string logStoragePath) => new()
    {
        MonitoringPaths = new[] { monitoringPath },
        LogStoragePath = logStoragePath,
        FlushIntervalSeconds = 5,
        NoiseFilter = NoiseFilterProfile.Standard
    };

    /// <summary>Creates the default user settings.</summary>
    public static UserSettings CreateUser() => new();
}

/// <summary>Describes a settings validation failure.</summary>
public sealed record SettingsValidationError(string Property, string Message);

/// <summary>Contains all validation errors found in one settings object.</summary>
public sealed record SettingsValidationResult(IReadOnlyList<SettingsValidationError> Errors)
{
    /// <summary>Gets whether validation succeeded.</summary>
    public bool IsValid => Errors.Count == 0;
    /// <summary>Creates a successful result.</summary>
    public static SettingsValidationResult Success { get; } = new(Array.Empty<SettingsValidationError>());
}

/// <summary>Provides the result of loading a versioned settings document.</summary>
public sealed record SettingsLoadResult<T>(T Settings, bool Recovered, bool UsedPreviousVersion, string? Warning);

/// <summary>Represents a settings change retained as a history fact without secrets or file content.</summary>
public sealed record SettingsChangeHistoryEvent(
    string Scope,
    DateTimeOffset ChangedAtUtc,
    IReadOnlyList<string> ChangedProperties);

/// <summary>Represents the result returned by the agent after an apply request.</summary>
public sealed record SettingsApplyResult(bool Succeeded, bool RestartRequired, bool RestartCompleted, string? Error)
{
    /// <summary>Creates a failed apply result.</summary>
    public static SettingsApplyResult Failed(string error) => new(false, false, false, error);
}

