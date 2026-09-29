using System.Text.Json;

namespace StorageChronicle.Agent.Tests;

internal static class AgentTestFixtureOwnership
{
    private const string MarkerName = ".test-owner.json";
    private const string MarkerSchema = "StorageChronicle.TestFixtureOwner.v1";

    public static string CreateTempRoot(string category, out string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        if (category.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || category.Contains(Path.DirectorySeparatorChar) || category.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("The fixture category must be one path component.", nameof(category));
        runId = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), category, runId);
        Directory.CreateDirectory(root);
        WriteMarker(root, runId);
        return root;
    }

    public static void DeleteTempRoot(string root, string category, string runId)
    {
        if (!Directory.Exists(root)) return;
        var fullRoot = Path.GetFullPath(root);
        var expectedParent = Path.Combine(Path.GetTempPath(), category);
        if (!string.Equals(Path.GetDirectoryName(fullRoot), expectedParent, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(fullRoot) != runId || !Guid.TryParseExact(runId, "N", out _))
            throw new IOException("The Agent test fixture escaped its dedicated temp parent or run GUID.");
        ValidateMarker(fullRoot, runId);
        Directory.Delete(fullRoot, recursive: true);
    }

    public static string CreateMarkedScenario(string parent, string name, out string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var fullParent = Path.GetFullPath(parent);
        var parentVolume = Path.GetPathRoot(fullParent);
        if (string.IsNullOrWhiteSpace(parentVolume) || string.Equals(parentVolume, "C:\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The acceptance scenario refuses the guest system volume.");
        runId = Guid.NewGuid().ToString("N");
        var scenario = Path.Combine(fullParent, $"StorageChronicle.Acceptance.{name}.{runId}");
        Directory.CreateDirectory(scenario);
        WriteMarker(scenario, runId);
        return scenario;
    }

    public static void DeleteMarkedScenario(string scenario, string allowedRoot)
    {
        if (!Directory.Exists(scenario)) return;
        var fullScenario = Path.GetFullPath(scenario);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));
        if (!fullScenario.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The acceptance scenario escaped its configured test root.");
        var runId = Path.GetFileName(fullScenario).Split('.').LastOrDefault();
        if (runId is null || !Guid.TryParseExact(runId, "N", out _))
            throw new IOException("The acceptance scenario name does not end in a run GUID.");
        ValidateMarker(fullScenario, runId);
        Directory.Delete(fullScenario, recursive: true);
    }

    private static void WriteMarker(string root, string runId)
    {
        using var marker = new FileStream(Path.Combine(root, MarkerName), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(marker, new { Schema = MarkerSchema, RunId = runId });
        marker.Flush(flushToDisk: true);
    }

    private static void ValidateMarker(string root, string runId)
    {
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, MarkerName)));
        if (marker.RootElement.GetProperty("Schema").GetString() != MarkerSchema || marker.RootElement.GetProperty("RunId").GetString() != runId)
            throw new IOException("The Agent test fixture ownership marker does not match this run.");
    }
}
