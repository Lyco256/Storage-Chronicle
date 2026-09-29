using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using Xunit;

namespace StorageChronicle.Agent.Tests;

public sealed class IpcProtocolTests
{
    [Fact]
    public void RoundTripUsesSourceGeneratedEnvelopeAndLengthPrefix()
    {
        var codec = new LengthPrefixedJsonCodec();
        var frame = codec.Encode(IpcProtocol.Create("AgentHealthRequest", new AgentHealthRequest()), IpcProtocol.Major, IpcProtocol.Minor);
        var envelope = codec.Decode<IpcEnvelope>(frame, IpcProtocol.Major);
        Assert.Equal("AgentHealthRequest", envelope.MessageType);
        Assert.IsType<AgentHealthRequest>(IpcProtocol.Read<AgentHealthRequest>(envelope));
    }

    [Fact]
    public void UnsupportedMajorAndMismatchedLengthAreRejected()
    {
        var codec = new LengthPrefixedJsonCodec();
        var frame = codec.Encode(IpcProtocol.Create("AgentHealthRequest", new AgentHealthRequest()), IpcProtocol.Major, IpcProtocol.Minor);
        Assert.Throws<InvalidDataException>(() => codec.Decode<IpcEnvelope>(frame, IpcProtocol.Major + 1));
        var malformed = frame[..^1];
        Assert.Throws<InvalidDataException>(() => codec.Decode<IpcEnvelope>(malformed, IpcProtocol.Major));
    }

    [Fact]
    public void RecursiveEventStackAndDiffFilterContractsRoundTrip()
    {
        var id = EventId.New();
        var child = new EventStackNodeSnapshot("child", "Source", new EventStackRow(id, DateTimeOffset.UtcNow, "C:/x", CanonicalOperation.Create, "x", EventQuality.Exact, null, ProcessAttributionQuality.Unknown, EventOrigin.LiveUsn, Array.Empty<EventId>()), "Unknown process", Array.Empty<EventStackNodeSnapshot>());
        var request = new DiffProjectionRequest(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, DiffMode.Period, Page: 2, PageSize: 50, AllFilters: ["report"], AnyFilters: ["txt", "doc"], ExcludeFilters: ["temporary"]);
        var nodeEnvelope = IpcProtocol.Create("ProjectionPageResponse", new ProjectionPageResponse([child.Row], 1, 50, 1, false, [child]));
        var requestEnvelope = IpcProtocol.Create("DiffProjectionRequest", request);

        var page = IpcProtocol.Read<ProjectionPageResponse>(nodeEnvelope);
        var decoded = IpcProtocol.Read<DiffProjectionRequest>(requestEnvelope);
        Assert.Equal("child", Assert.Single(page.Nodes!).NodeId);
        Assert.Equal(["report"], decoded.AllFilters);
        Assert.Equal(["txt", "doc"], decoded.AnyFilters);
        Assert.Equal(["temporary"], decoded.ExcludeFilters);
    }

    [Fact]
    public void ClientHelloRoundTripsItsRoleAndSession()
    {
        var codec = new LengthPrefixedJsonCodec();
        var frame = codec.Encode(IpcProtocol.Create("ClientHello", new IpcClientHello(IpcClientRole.SessionAgent, 42)), IpcProtocol.Major, IpcProtocol.Minor);
        var envelope = codec.Decode<IpcEnvelope>(frame, IpcProtocol.Major);
        var hello = IpcProtocol.Read<IpcClientHello>(envelope);

        Assert.Equal(IpcClientRole.SessionAgent, hello.Role);
        Assert.Equal(42, hello.SessionId);
    }
}
