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
        ValidateLogStorageIsolation(settings, errors);
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

        var consentIdentities = new HashSet<string>(StringComparer.Ordinal);
        if (settings.MediaMirrorConsents is null)
        {
            errors.Add(new("MediaMirrorConsents", "Consent collection cannot be null."));
        }
        else
        {
            foreach (var consent in settings.MediaMirrorConsents)
            {
                if (consent is null)
                {
                    errors.Add(new("MediaMirrorConsents", "Consent entries cannot be null."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(consent.PcIdentity) || string.IsNullOrWhiteSpace(consent.LogicalMediaId) ||
                    string.IsNullOrWhiteSpace(consent.LiveVolumeIdentity) || string.IsNullOrWhiteSpace(consent.DedicatedMediaRootIdentity))
                {
                    errors.Add(new("MediaMirrorConsents", "Consent must bind PC, logical media, live volume, and dedicated root identities."));
                    continue;
                }

                var key = $"{consent.PcIdentity}\0{consent.LogicalMediaId}";
                if (!consentIdentities.Add(key)) errors.Add(new("MediaMirrorConsents", "Only one current consent binding is allowed per PC and logical media identity."));

                var ntfs = string.Equals(consent.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);
                var knownNonNtfs = new[] { "FAT", "FAT32", "exFAT", "Other" }.Contains(consent.FileSystem, StringComparer.OrdinalIgnoreCase);
                var validProtection = ntfs
                    ? consent.AclProtection is "NtfsAclVerified" or "NtfsAclUnavailable"
                    : knownNonNtfs && consent.AclProtection == "NotProvidedByFileSystem";
                if (!validProtection)
                    errors.Add(new("MediaMirrorConsents", "Filesystem and ACL-protection classifications are missing, unknown, or inconsistent."));
                if (consent.ApprovedAtUtc == default)
                    errors.Add(new("MediaMirrorConsents", "Consent must record a non-default approval timestamp."));
            }
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

        if (!Enum.IsDefined(settings.InitialEventStackMode))
        {
            errors.Add(new("InitialEventStackMode", "Initial Event Stack mode is not supported."));
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

    private static void ValidateLogStorageIsolation(MachineSettings settings, List<SettingsValidationError> errors)
    {
        var storagePath = settings.LogStoragePath;
        if (string.IsNullOrWhiteSpace(storagePath) || !Path.IsPathRooted(storagePath)) return;
        if (storagePath.StartsWith("\\\\", StringComparison.Ordinal) || storagePath.StartsWith("//", StringComparison.Ordinal))
        {
            errors.Add(new("LogStoragePath", "History storage must be a local path; network/UNC locations are not accepted."));
            return;
        }

        foreach (var monitoringPath in settings.MonitoringPaths)
        {
            if (string.IsNullOrWhiteSpace(monitoringPath) || !Path.IsPathRooted(monitoringPath)) continue;
            try
            {
                if (PathsOverlap(storagePath, monitoringPath))
                {
                    errors.Add(new("LogStoragePath", $"History storage must not equal, contain, or be contained by monitored path '{monitoringPath}'."));
                }
            }
            catch (ArgumentException)
            {
                // The regular path validator reports malformed paths independently.
            }
            catch (NotSupportedException)
            {
                // The regular path validator reports malformed paths independently.
            }
            catch (PathTooLongException)
            {
                // The regular path validator reports malformed paths independently.
            }
        }
    }

    private static bool PathsOverlap(string left, string right)
    {
        var leftFull = Path.GetFullPath(left);
        var rightFull = Path.GetFullPath(right);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return IsSameOrDescendant(leftFull, rightFull, comparison) || IsSameOrDescendant(rightFull, leftFull, comparison);
    }

    private static bool IsSameOrDescendant(string path, string possibleParent, StringComparison comparison)
    {
        if (string.Equals(path, possibleParent, comparison)) return true;
        var parentPrefix = Path.EndsInDirectorySeparator(possibleParent)
            ? possibleParent
            : possibleParent + Path.DirectorySeparatorChar;
        return path.StartsWith(parentPrefix, comparison);
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
