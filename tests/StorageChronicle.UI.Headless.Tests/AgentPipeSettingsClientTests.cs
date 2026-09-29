using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Settings;
using StorageChronicle.UI.Shared;
using Xunit;

namespace StorageChronicle.UI.Headless.Tests;

public sealed class AgentPipeSettingsClientTests
{
    [Fact]
    public async Task SettingsGatewayLoadsBothScopesAndRecoveryWarnings()
    {
        var pipeName = "StorageChronicle.SettingsTest." + Guid.NewGuid().ToString("N");
        var machine = new MachineSettings { MonitoringPaths = ["C:\\watched"], LogStoragePath = "C:\\history" };
        var user = new UserSettings { InitialEventStackMode = EventStackInitialMode.Grouped };
        var server = ServeOnceAsync(pipeName, request =>
        {
            Assert.Equal("SettingsSnapshotRequest", request.MessageType);
            var snapshot = new SettingsSnapshot(
                JsonSerializer.SerializeToElement(machine),
                JsonSerializer.SerializeToElement(user),
                "machine recovered",
                null);
            return IpcProtocol.Create("SettingsSnapshot", snapshot);
        });

        IAgentSettingsGateway gateway = new AgentPipeProjectionClient(pipeName);
        var loadedMachine = await gateway.LoadMachineSettingsAsync();
        var requestSeen = await server;

        Assert.Equal("SettingsSnapshotRequest", requestSeen.MessageType);
        Assert.Equal(machine.MonitoringPaths, loadedMachine.Settings.MonitoringPaths);
        Assert.Equal("machine recovered", loadedMachine.Warning);
        Assert.True(loadedMachine.Recovered);
    }

    [Fact]
    public async Task SettingsGatewayUpdatesMachineScopeOverIpc()
    {
        var pipeName = "StorageChronicle.SettingsTest." + Guid.NewGuid().ToString("N");
        var proposed = new MachineSettings { MonitoringPaths = ["C:\\watch"], LogStoragePath = "C:\\history" };
        var server = ServeOnceAsync(pipeName, request =>
        {
            Assert.Equal("SettingsUpdateRequest", request.MessageType);
            var update = IpcProtocol.Read<SettingsUpdateRequest>(request);
            Assert.Equal(SettingsScope.Machine, update.Scope);
            var received = JsonSerializer.Deserialize<MachineSettings>(update.Settings.GetRawText());
            Assert.NotNull(received);
            Assert.Equal(proposed.MonitoringPaths, received!.MonitoringPaths);
            return IpcProtocol.Create("SettingsApplyResult", JsonSerializer.SerializeToElement(new SettingsApplyResult(true, true, true, null)));
        });

        IAgentSettingsGateway gateway = new AgentPipeProjectionClient(pipeName);
        var result = await gateway.ApplyMachineSettingsAsync(proposed);
        var requestSeen = await server;

        Assert.Equal("SettingsUpdateRequest", requestSeen.MessageType);
        Assert.True(result.Succeeded);
        Assert.True(result.RestartRequired);
        Assert.True(result.RestartCompleted);
    }

    [Fact]
    public async Task UserSettingsLoadUsesTheSettingsSnapshotIpcRequest()
    {
        var pipeName = "StorageChronicle.SettingsTest." + Guid.NewGuid().ToString("N");
        var userSettings = new UserSettings { EventStackPageSize = 137, EventStackSort = EventStackSortOrder.OldestFirst };
        var server = ServeOnceAsync(pipeName, request =>
        {
            Assert.Equal("SettingsSnapshotRequest", request.MessageType);
            var snapshot = new SettingsSnapshot(
                JsonSerializer.SerializeToElement(new MachineSettings()),
                JsonSerializer.SerializeToElement(userSettings),
                null,
                null);
            return IpcProtocol.Create("SettingsSnapshot", snapshot);
        });

        var client = new AgentPipeProjectionClient(pipeName);
        var loaded = await client.LoadUserSettingsAsync();
        var requestSeen = await server;

        Assert.Equal("SettingsSnapshotRequest", requestSeen.MessageType);
        Assert.Equal(137, loaded.EventStackPageSize);
        Assert.Equal(EventStackSortOrder.OldestFirst, loaded.EventStackSort);
    }

    [Fact]
    public async Task UserSettingsUpdateUsesUserScopeAndReturnsAgentValidationResult()
    {
        var pipeName = "StorageChronicle.SettingsTest." + Guid.NewGuid().ToString("N");
        var proposed = new UserSettings { EventStackPageSize = 4999, SavedFilters = ["safe-filter"] };
        var server = ServeOnceAsync(pipeName, request =>
        {
            Assert.Equal("SettingsUpdateRequest", request.MessageType);
            var update = IpcProtocol.Read<SettingsUpdateRequest>(request);
            Assert.Equal(SettingsScope.User, update.Scope);
            var received = JsonSerializer.Deserialize<UserSettings>(update.Settings.GetRawText());
            Assert.NotNull(received);
            Assert.Equal(4999, received!.EventStackPageSize);
            Assert.Equal("safe-filter", Assert.Single(received.SavedFilters));
            var result = new SettingsApplyResult(true, false, false, null);
            return IpcProtocol.Create("SettingsApplyResult", JsonSerializer.SerializeToElement(result));
        });

        var client = new AgentPipeProjectionClient(pipeName);
        var result = await client.ApplyUserSettingsAsync(proposed);
        var requestSeen = await server;

        Assert.Equal("SettingsUpdateRequest", requestSeen.MessageType);
        Assert.True(result.Succeeded);
        Assert.False(result.RestartRequired);
    }

    private static async Task<IpcEnvelope> ServeOnceAsync(string pipeName, Func<IpcEnvelope, IpcEnvelope> respond)
    {
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync();
        var codec = new LengthPrefixedJsonCodec();
        var request = await ReadEnvelopeAsync(server, codec);
        var response = respond(request);
        var frame = codec.Encode(response, IpcProtocol.Major, IpcProtocol.Minor);
        await server.WriteAsync(frame);
        await server.FlushAsync();
        return request;
    }

    private static async Task<IpcEnvelope> ReadEnvelopeAsync(Stream stream, LengthPrefixedJsonCodec codec)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > LengthPrefixedJsonCodec.MaximumPayloadBytes) throw new InvalidDataException("Test received an invalid IPC frame length.");
        var frame = new byte[sizeof(int) + length];
        header.CopyTo(frame, 0);
        await ReadExactlyAsync(stream, frame.AsMemory(sizeof(int)));
        return codec.Decode<IpcEnvelope>(frame, IpcProtocol.Major);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..]);
            if (read == 0) throw new EndOfStreamException("Test IPC stream ended before the frame was complete.");
            offset += read;
        }
    }
}
