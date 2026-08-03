using System.Buffers.Binary;
using System.IO.Pipes;
using StorageChronicle.Contracts.Runtime;
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

    [Fact]
    public async Task UnpublishedSessionAgentRoleIsRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StorageChronicle.AgentPipe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new AppendOnlyStorageEngine(new StorageEngineOptions(directory) { FlushInterval = TimeSpan.FromMinutes(1) });
            using var server = new NamedPipeAgentServer(new AgentProjectionService(store), store, new AgentHealthState(), new EventNormalizer());
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.StartAsync(cancellation.Token);

            await using var client = new NamedPipeClientStream(".", NamedPipeAgentServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5_000, cancellation.Token);
            var codec = new LengthPrefixedJsonCodec();
            var frame = codec.Encode(IpcProtocol.Create("ClientHello", new IpcClientHello(IpcClientRole.SessionAgent, System.Diagnostics.Process.GetCurrentProcess().SessionId)), IpcProtocol.Major, IpcProtocol.Minor);
            await client.WriteAsync(frame, cancellation.Token);
            await client.FlushAsync(cancellation.Token);

            var responseFrame = await ReadFrameAsync(client, cancellation.Token);
            var response = codec.Decode<IpcEnvelope>(responseFrame, IpcProtocol.Major);
            Assert.Equal("Error", response.MessageType);
            Assert.Equal("Rejected", IpcProtocol.Read<AgentHealth>(response).State);

            await server.StopAsync(CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        Assert.InRange(length, 0, LengthPrefixedJsonCodec.MaximumPayloadBytes);
        var frame = new byte[sizeof(int) + length];
        header.CopyTo(frame, 0);
        if (length > 0) await stream.ReadExactlyAsync(frame.AsMemory(sizeof(int), length), cancellationToken);
        return frame;
    }
}
