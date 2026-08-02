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
        var directory = Path.Combine(Path.GetTempPath(), "StorageChronicle.AgentPipe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
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
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
