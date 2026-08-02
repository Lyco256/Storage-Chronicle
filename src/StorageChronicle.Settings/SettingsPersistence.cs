using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StorageChronicle.Settings;

/// <summary>Provides the filesystem operations needed by the atomic settings store.</summary>
public interface ISettingsFileSystem
{
    /// <summary>Returns whether a file exists.</summary>
    bool FileExists(string path);
    /// <summary>Reads strict UTF-8 text.</summary>
    string ReadUtf8(string path);
    /// <summary>Writes UTF-8 without a BOM and flushes the file to stable storage.</summary>
    void WriteUtf8Flushed(string path, string content);
    /// <summary>Replaces a target and retains its previous generation as a backup.</summary>
    void ReplaceAtomically(string temporaryPath, string targetPath, string backupPath);
    /// <summary>Moves a new file into an unused target path atomically.</summary>
    void MoveAtomically(string temporaryPath, string targetPath);
    /// <summary>Deletes a file if it exists.</summary>
    void DeleteIfExists(string path);
}

/// <summary>Uses the local filesystem while preserving the same-directory atomicity invariant.</summary>
public sealed class PhysicalSettingsFileSystem : ISettingsFileSystem
{
    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public string ReadUtf8(string path) => File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));

    /// <inheritdoc />
    public void WriteUtf8Flushed(string path, string content)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <inheritdoc />
    public void ReplaceAtomically(string temporaryPath, string targetPath, string backupPath)
    {
        File.Replace(temporaryPath, targetPath, backupPath, ignoreMetadataErrors: true);
    }

    /// <inheritdoc />
    public void MoveAtomically(string temporaryPath, string targetPath) => File.Move(temporaryPath, targetPath);

    /// <inheritdoc />
    public void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

/// <summary>Loads and saves one versioned settings document.</summary>
public interface ISettingsStore<T>
{
    /// <summary>Loads the primary document or a valid previous generation.</summary>
    SettingsLoadResult<T> Load();
    /// <summary>Validates and atomically saves one new document.</summary>
    void Save(T settings);
}

/// <summary>Stores machine settings at the machine settings path.</summary>
public sealed class MachineSettingsStore : ISettingsStore<MachineSettings>
{
    private readonly VersionedJsonSettingsStore<MachineSettings> inner;

    /// <summary>Initializes a machine settings store with the Windows default path.</summary>
    public MachineSettingsStore()
        : this(new WindowsSettingsPathProvider())
    {
    }

    /// <summary>Initializes a machine settings store with a path provider and optional filesystem.</summary>
    public MachineSettingsStore(ISettingsPathProvider paths, ISettingsFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        inner = new(paths.MachineSettingsPath, DefaultSettings.CreateMachine(Environment.CurrentDirectory, Environment.CurrentDirectory), SettingsValidator.EnsureValid, fileSystem ?? new PhysicalSettingsFileSystem());
    }

    /// <inheritdoc />
    public SettingsLoadResult<MachineSettings> Load() => inner.Load();
    /// <inheritdoc />
    public void Save(MachineSettings settings) => inner.Save(settings);
}

/// <summary>Stores user settings at the user settings path.</summary>
public sealed class UserSettingsStore : ISettingsStore<UserSettings>
{
    private readonly VersionedJsonSettingsStore<UserSettings> inner;

    /// <summary>Initializes a user settings store with the Windows default path.</summary>
    public UserSettingsStore()
        : this(new WindowsSettingsPathProvider())
    {
    }

    /// <summary>Initializes a user settings store with a path provider and optional filesystem.</summary>
    public UserSettingsStore(ISettingsPathProvider paths, ISettingsFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        inner = new(paths.UserSettingsPath, DefaultSettings.CreateUser(), SettingsValidator.EnsureValid, fileSystem ?? new PhysicalSettingsFileSystem());
    }

    /// <inheritdoc />
    public SettingsLoadResult<UserSettings> Load() => inner.Load();
    /// <inheritdoc />
    public void Save(UserSettings settings) => inner.Save(settings);
}

internal sealed class VersionedJsonSettingsStore<T>
{
    private const int CurrentSchema = 1;
    private readonly string path;
    private readonly string backupPath;
    private readonly T defaults;
    private readonly Action<T> validate;
    private readonly ISettingsFileSystem fileSystem;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public VersionedJsonSettingsStore(string path, T defaults, Action<T> validate, ISettingsFileSystem fileSystem)
    {
        this.path = path;
        backupPath = path + ".bak";
        this.defaults = defaults;
        this.validate = validate;
        this.fileSystem = fileSystem;
    }

    public SettingsLoadResult<T> Load()
    {
        var primary = TryRead(path, usedPreviousVersion: false);
        if (primary is not null)
        {
            return primary;
        }

        var backup = TryRead(backupPath, usedPreviousVersion: true);
        if (backup is not null)
        {
            return backup with { Recovered = true, Warning = "The primary settings document was invalid; the previous valid version was restored." };
        }

        return new(defaults, Recovered: true, UsedPreviousVersion: false, Warning: "Both settings generations were invalid or unavailable; defaults were loaded.");
    }

    public void Save(T settings)
    {
        validate(settings);
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException("Settings path must have a directory.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var document = new SettingsDocument<T> { SchemaVersion = CurrentSchema, Settings = settings };
        var json = JsonSerializer.Serialize(document, JsonOptions);
        try
        {
            fileSystem.WriteUtf8Flushed(temporaryPath, json);
            if (fileSystem.FileExists(path))
            {
                fileSystem.ReplaceAtomically(temporaryPath, path, backupPath);
            }
            else
            {
                fileSystem.MoveAtomically(temporaryPath, path);
            }
        }
        catch
        {
            fileSystem.DeleteIfExists(temporaryPath);
            throw;
        }
    }

    private SettingsLoadResult<T>? TryRead(string candidate, bool usedPreviousVersion)
    {
        if (!fileSystem.FileExists(candidate))
        {
            return null;
        }

        try
        {
            var json = fileSystem.ReadUtf8(candidate);
            var (settings, schemaVersion) = Deserialize(json);
            if (schemaVersion > CurrentSchema)
            {
                return null;
            }

            validate(settings);
            return new(settings, Recovered: false, UsedPreviousVersion: usedPreviousVersion, Warning: schemaVersion < CurrentSchema ? "The settings document was migrated to the current schema." : null);
        }
        catch (Exception) when (candidate == path || candidate == backupPath)
        {
            return null;
        }
    }

    private static (T Settings, int SchemaVersion) Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Settings document must be an object.");
        }

        if (!document.RootElement.TryGetProperty("settings", out _))
        {
            var legacy = document.RootElement.Deserialize<T>(JsonOptions) ?? throw new JsonException("Legacy settings document was empty.");
            return (legacy, 0);
        }

        var current = document.RootElement.Deserialize<SettingsDocument<T>>(JsonOptions) ?? throw new JsonException("Settings document was empty.");
        return (current.Settings ?? throw new JsonException("Settings payload was empty."), current.SchemaVersion);
    }

    private sealed class SettingsDocument<TSettings>
    {
        public int SchemaVersion { get; set; }
        public TSettings? Settings { get; set; }
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? UnknownFields { get; set; }
    }
}

