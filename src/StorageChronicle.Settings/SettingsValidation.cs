using System.Globalization;

namespace StorageChronicle.Settings;

/// <summary>Validates all persisted machine and user settings before serialization.</summary>
public static class SettingsValidator
{
    /// <summary>Validates machine settings and all configured paths.</summary>
    public static SettingsValidationResult Validate(MachineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<SettingsValidationError>();
        ValidatePaths(settings.MonitoringPaths, "MonitoringPaths", errors);
        ValidatePaths(settings.ExcludedPaths, "ExcludedPaths", errors);
        ValidatePath(settings.LogStoragePath, "LogStoragePath", errors);
        if (settings.FlushIntervalSeconds is < 1 or > 60)
        {
            errors.Add(new("FlushIntervalSeconds", "Flush interval must be between 1 and 60 seconds."));
        }

        foreach (var pair in settings.MediaMirrors)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                errors.Add(new("MediaMirrors", "Every mirror must have a media identifier."));
            }
            ValidatePath(pair.Value, $"MediaMirrors[{pair.Key}]", errors);
        }

        return new(errors);
    }

    /// <summary>Validates user settings and their bounded numeric ranges.</summary>
    public static SettingsValidationResult Validate(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<SettingsValidationError>();
        ValidateSeconds(settings.ActivityGroupTimeoutSeconds, "ActivityGroupTimeoutSeconds", errors);
        ValidateSeconds(settings.PaneTimeoutSeconds, "PaneTimeoutSeconds", errors);
        if (settings.EventStackPageSize is < 50 or > 5000)
        {
            errors.Add(new("EventStackPageSize", "Event Stack page size must be between 50 and 5000."));
        }

        if (settings.DiffZoomPercent is < 50 or > 300)
        {
            errors.Add(new("DiffZoomPercent", "Diff View zoom must be between 50 and 300 percent."));
        }

        for (var index = 0; index < settings.SavedFilters.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(settings.SavedFilters[index]))
            {
                errors.Add(new($"SavedFilters[{index}]", "Saved filters cannot be empty."));
            }
        }

        return new(errors);
    }

    /// <summary>Throws a descriptive exception when machine settings are invalid.</summary>
    public static void EnsureValid(MachineSettings settings) => ThrowIfInvalid(Validate(settings));

    /// <summary>Throws a descriptive exception when user settings are invalid.</summary>
    public static void EnsureValid(UserSettings settings) => ThrowIfInvalid(Validate(settings));

    private static void ValidateSeconds(double value, string property, List<SettingsValidationError> errors)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.5 || value > 60)
        {
            errors.Add(new(property, "Timeout must be between 0.5 and 60 seconds."));
        }
    }

    private static void ValidatePaths(IReadOnlyList<string> paths, string property, List<SettingsValidationError> errors)
    {
        for (var index = 0; index < paths.Count; index++)
        {
            ValidatePath(paths[index], $"{property}[{index}]", errors);
        }
    }

    private static void ValidatePath(string path, string property, List<SettingsValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add(new(property, "A path is required."));
            return;
        }

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Contains('*', StringComparison.Ordinal) || path.Contains('?', StringComparison.Ordinal))
        {
            errors.Add(new(property, "The path contains invalid characters."));
            return;
        }

        if (!Path.IsPathRooted(path))
        {
            errors.Add(new(property, "The path must be absolute."));
        }
    }

    private static void ThrowIfInvalid(SettingsValidationResult result)
    {
        if (!result.IsValid)
        {
            throw new SettingsValidationException(result);
        }
    }
}

/// <summary>Raised when settings do not satisfy their persisted schema constraints.</summary>
public sealed class SettingsValidationException : ArgumentException
{
    /// <summary>Initializes an exception containing every validation error.</summary>
    public SettingsValidationException(SettingsValidationResult result)
        : base(string.Join("; ", result.Errors.Select(error => $"{error.Property}: {error.Message}"))) => Result = result;

    /// <summary>Gets the structured validation result.</summary>
    public SettingsValidationResult Result { get; }
}
