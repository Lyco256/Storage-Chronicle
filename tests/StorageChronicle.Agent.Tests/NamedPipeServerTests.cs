using System.Collections.Immutable;
using System.Buffers.Binary;
using System.IO.Pipes;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Agent;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Normalization;
using StorageChronicle.Platform.Windows.FileSystem.Snapshot;
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

    [Fact]
    public async Task ExecuteDecisionDelegatesTheSelectedGapToTheConfirmedRunner()
    {
        var directory = Path.Combine(Path.GetTempPath(), "StorageChronicle.AgentPipe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new AppendOnlyStorageEngine(new StorageEngineOptions(directory) { FlushInterval = TimeSpan.FromMinutes(1) });
            var health = new AgentHealthState();
            var runner = new RecordingReconciliationRunner();
            using var server = new NamedPipeAgentServer(new AgentProjectionService(store), store, health, new EventNormalizer(), reconciliationRunner: runner);
            var volume = new VolumeDescriptor(VolumeId.Create("execute-volume"), "FAT32", [directory], false, true, false, false, true);
            var gap = new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.LiveUsn, volume.Id, null, null, null, null, CanonicalOperation.UnverifiedGap, null,
                new EventTime(DateTimeOffset.UtcNow, TimeSpan.Zero, null, DateTimeOffset.UtcNow, new SourceSequence(1), new MountSequence(1)), EventQuality.UnverifiedGap, null, ProcessAttributionQuality.Unknown, null, "gap",
                ImmutableDictionary<string, string>.Empty.Add("reason", "test"));
            health.Observe(gap);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.StartAsync(cancellation.Token);

            await using var client = new NamedPipeClientStream(".", NamedPipeAgentServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5_000, cancellation.Token);
            var codec = new LengthPrefixedJsonCodec();
            await client.WriteAsync(codec.Encode(IpcProtocol.Create("ClientHello", new IpcClientHello(IpcClientRole.DesktopUi, System.Diagnostics.Process.GetCurrentProcess().SessionId)), IpcProtocol.Major, IpcProtocol.Minor), cancellation.Token);
            await client.FlushAsync(cancellation.Token);
            var request = IpcProtocol.Create("ReconciliationDecision", new ReconciliationDecision(gap.EventId.ToString(), true));
            await client.WriteAsync(codec.Encode(request, IpcProtocol.Major, IpcProtocol.Minor), cancellation.Token);
            await client.FlushAsync(cancellation.Token);

            var response = codec.Decode<IpcEnvelope>(await ReadFrameAsync(client, cancellation.Token), IpcProtocol.Major);
            Assert.Equal("AgentHealth", response.MessageType);
            Assert.True(runner.Request is not null);
            Assert.Equal(volume.Id, runner.Request!.VolumeId);
            Assert.Empty(IpcProtocol.Read<AgentHealth>(response).PendingReconciliations!);
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

    private sealed class RecordingReconciliationRunner : IConfirmedReconciliationRunner
    {
        public PendingReconciliationRequest? Request { get; private set; }

        public ValueTask<ReconciliationExecutionSummary> ExecuteAsync(PendingReconciliationRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new ReconciliationExecutionSummary("test-run", request.VolumeId!.Value, "FAT32", true, "Completed", 0, 0, 0, 0, 0, 0, 0, new ReconciliationPriorityResult(false, null, null, 0, 0, 0), now, now, null));
        }
    }
}
