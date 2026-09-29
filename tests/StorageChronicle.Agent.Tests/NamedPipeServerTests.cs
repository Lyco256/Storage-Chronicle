using StorageChronicle.Agent;
using StorageChronicle.Normalization;
using StorageChronicle.Storage;
using StorageChronicle.UI.Shared;
using Xunit;

namespace StorageChronicle.Agent.Tests;

public sealed class NamedPipeServerTests
{
    [Fact]
    public async Task ProductionPipeClientReadsHealthAndServerRemainsAvailableAfterDisconnect()
    {
        var runId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "StorageChronicle.AgentPipe", runId);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, ".test-owner"), runId, TestContext.Current.CancellationToken);
        try
        {
            await using var store = new AppendOnlyStorageEngine(new StorageEngineOptions(directory) { FlushInterval = TimeSpan.FromMinutes(1) });
            using var server = new NamedPipeAgentServer(new AgentProjectionService(store), store, new AgentHealthState(), new EventNormalizer());
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.StartAsync(cancellation.Token);

            var client = new AgentPipeProjectionClient();
            var first = await client.GetHealthAsync(cancellation.Token);
            Assert.Equal("Running", first.State);

            var second = await client.GetHealthAsync(cancellation.Token);
            Assert.Equal("Running", second.State);

            await server.StopAsync(CancellationToken.None);
        }
        finally
        {
            CleanupFixture(directory);
        }
    }

    private static void CleanupFixture(string directory)
    {
        if (!Directory.Exists(directory)) return;
        var owner = Path.Combine(directory, ".test-owner");
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), Path.GetFullPath(Path.Combine(Path.GetTempPath(), "StorageChronicle.AgentPipe")), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || !File.Exists(owner) || !string.Equals(File.ReadAllText(owner), Path.GetFileName(directory), StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to clean an Agent pipe fixture without its run owner marker.");
        var boundary = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            var resolved = Path.GetFullPath(entry);
            if (!resolved.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to clean an Agent pipe fixture with an out-of-root path or reparse point.");
        }
        Directory.Delete(directory, recursive: true);
    }
}
