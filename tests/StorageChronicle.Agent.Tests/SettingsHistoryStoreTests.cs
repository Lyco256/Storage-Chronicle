using System.Text.Json;
using StorageChronicle.Agent;
using StorageChronicle.Settings;
using Xunit;

namespace StorageChronicle.Agent.Tests;

public sealed class SettingsHistoryStoreTests
{
    [Fact]
    public async Task SettingsHistoryUsesAnOwnedDirectoryAndRefusesUnknownExistingContent()
    {
        var runId = Guid.NewGuid().ToString("N");
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "StorageChronicle.SettingsHistory.Tests", runId);
        var settingsRoot = Path.Combine(fixtureRoot, "settings");
        var historyPath = Path.Combine(settingsRoot, "settings-history.ndjson");
        try
        {
            Directory.CreateDirectory(fixtureRoot);
            using (var owner = new FileStream(Path.Combine(fixtureRoot, ".test-owner.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                JsonSerializer.Serialize(owner, new { Schema = "StorageChronicle.TestFixtureOwner.v1", RunId = runId });
            using (var store = new SettingsHistoryStore(historyPath))
            {
                await store.RecordAsync(new SettingsChangeHistoryEvent("Machine", DateTimeOffset.UtcNow, ["FlushIntervalSeconds"]), TestContext.Current.CancellationToken);
            }

            var markerPath = Path.Combine(settingsRoot, ".settings-history-owner.json");
            Assert.True(File.Exists(markerPath));
            Assert.True(File.Exists(historyPath));
            File.WriteAllText(Path.Combine(settingsRoot, "unrelated.txt"), "preserve");
            using var reopened = new SettingsHistoryStore(historyPath);
            await Assert.ThrowsAsync<IOException>(async () => await reopened.RecordAsync(new SettingsChangeHistoryEvent("Machine", DateTimeOffset.UtcNow, ["MonitoringPaths"]), TestContext.Current.CancellationToken));
            Assert.Equal("preserve", File.ReadAllText(Path.Combine(settingsRoot, "unrelated.txt")));
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                using var owner = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureRoot, ".test-owner.json")));
                Assert.Equal("StorageChronicle.TestFixtureOwner.v1", owner.RootElement.GetProperty("Schema").GetString());
                Assert.Equal(runId, owner.RootElement.GetProperty("RunId").GetString());
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
    }
}
