using System.Text.Json;
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
        var request = new DiffProjectionRequest(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, DiffMode.Period, Page: 2, PageSize: 50, AllFilters: ["report"], AnyFilters: ["txt", "doc"], ExcludeFilters: ["temporary"], ActivityFramesAscending: true);
        var nodeEnvelope = IpcProtocol.Create("ProjectionPageResponse", new ProjectionPageResponse([child.Row], 1, 50, 1, false, [child]));
        var requestEnvelope = IpcProtocol.Create("DiffProjectionRequest", request);

        var page = IpcProtocol.Read<ProjectionPageResponse>(nodeEnvelope);
        var decoded = IpcProtocol.Read<DiffProjectionRequest>(requestEnvelope);
        Assert.Equal("child", Assert.Single(page.Nodes!).NodeId);
        Assert.Equal(["report"], decoded.AllFilters);
        Assert.Equal(["txt", "doc"], decoded.AnyFilters);
        Assert.Equal(["temporary"], decoded.ExcludeFilters);
        Assert.True(decoded.ActivityFramesAscending);
    }

    [Fact]
    public void LegacyDiffProjectionRequestDefaultsNewActivityFramePagingFields()
    {
        using var payload = JsonDocument.Parse("""{"FromUtc":null,"ToUtc":"2026-09-29T00:00:00+00:00","Mode":0,"Page":1,"PageSize":500}""");
        var request = payload.RootElement.Deserialize<DiffProjectionRequest>(IpcJsonContext.Default.Options);

        Assert.NotNull(request);
        Assert.Equal(1, request.ActivityFramesPage);
        Assert.Equal(100, request.ActivityFramesPageSize);
        Assert.False(request.ActivityFramesAscending);
    }

    [Fact]
    public void DiffActivityFramesAndBoundedTimelineRoundTrip()
    {
        var eventId = EventId.New();
        var now = DateTimeOffset.UtcNow;
        var frame = new DiffActivityFrameSnapshot(
            "activity-1",
            ProcessInstanceId.Create("42:638000000000000000"),
            "editor.exe",
            ProcessAttributionQuality.Correlated,
            EventOrigin.LiveUsn,
            null,
            null,
            "C:/work",
            now.AddSeconds(-1),
            now,
            now.AddSeconds(5),
            false,
            1,
            1,
            0,
            new Dictionary<string, int> { [CanonicalOperation.Create.ToString()] = 1 },
            1,
            IsClosedByProcessExit: true);
        var response = new DiffProjectionResponse(Array.Empty<DiffEntry>(), ActivityFrames: [frame], ActivityFramesTotalCount: 1);
        var detailRequest = new DiffActivityFrameTimelineRequest(frame.FrameId, now.AddMinutes(-1), now, PageSize: 20);
        var envelope = IpcProtocol.Create("DiffProjectionResponse", response);
        var detailEnvelope = IpcProtocol.Create("DiffActivityFrameTimelineRequest", detailRequest);
        var decoded = IpcProtocol.Read<DiffProjectionResponse>(envelope);
        var decodedRequest = IpcProtocol.Read<DiffActivityFrameTimelineRequest>(detailEnvelope);
        var detailResponse = new DiffActivityFrameTimelineResponse(
            frame.FrameId,
            [new DiffActivityFrameEventSnapshot(eventId, now, "C:/work/file.txt", CanonicalOperation.Create, EventQuality.Exact)],
            1,
            20,
            1,
            false);
        var decodedDetail = IpcProtocol.Read<DiffActivityFrameTimelineResponse>(IpcProtocol.Create("DiffActivityFrameTimelineResponse", detailResponse));

        var actual = Assert.Single(decoded.ActivityFrames!);
        Assert.Equal(frame.FrameId, actual.FrameId);
        Assert.Equal(frame.ProcessId, actual.ProcessId);
        Assert.Equal(frame.CloseBoundaryUtc, actual.CloseBoundaryUtc);
        Assert.True(actual.IsClosedByProcessExit);
        Assert.Equal(1, actual.EventCount);
        Assert.Equal(frame.FrameId, decodedRequest.FrameId);
        Assert.Equal("C:/work/file.txt", Assert.Single(decodedDetail.Events).DisplayPath);
        Assert.Equal(1, actual.OperationBreakdown[CanonicalOperation.Create.ToString()]);
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

    [Fact]
    public void ConsentDecisionTrustRequiresExpectedUiImageInsideProtectedAgentInstallation()
    {
        const string install = @"C:\Program Files\Storage Chronicle";
        var expected = Path.Combine(install, "StorageChronicle.UI.Desktop.exe");

        Assert.True(NamedPipeClientIdentity.IsTrustedDesktopUiExecutable(expected, install, @"C:\Program Files"));
        Assert.False(NamedPipeClientIdentity.IsTrustedDesktopUiExecutable(Path.Combine(install, "untrusted.exe"), install, @"C:\Program Files"));
        Assert.False(NamedPipeClientIdentity.IsTrustedDesktopUiExecutable(Path.Combine(@"C:\Users\Public", "StorageChronicle.UI.Desktop.exe"), install, @"C:\Program Files"));
        Assert.False(NamedPipeClientIdentity.IsTrustedDesktopUiExecutable(expected, install, @"C:\Program Files (x86)"));
        Assert.False(NamedPipeClientIdentity.IsTrustedDesktopUiExecutable(expected, install, null));
    }

    [Fact]
    public void WindowsPcIdentityRequiresAndCanonicalizesAValidMachineGuid()
    {
        Assert.Equal("windows-machine-guid:0123456789abcdef0123456789abcdef",
            WindowsPcIdentity.FromMachineGuid("{01234567-89AB-CDEF-0123-456789ABCDEF}"));
        Assert.Throws<InvalidOperationException>(() => WindowsPcIdentity.FromMachineGuid(null));
        Assert.Throws<InvalidOperationException>(() => WindowsPcIdentity.FromMachineGuid("not-a-guid"));
        Assert.Throws<InvalidOperationException>(() => WindowsPcIdentity.FromMachineGuid(Guid.Empty.ToString()));
    }

    [Fact]
    public void MediaMirrorApprovalDisclosureAndDecisionRoundTrip()
    {
        var request = new PendingMediaMirrorApproval(
            "request-1", "pc-a", "media-a", "volume-a", "E:\\.StorageChronicle", "root-id-a", "exFAT",
            MediaMirrorAclDisclosure.NotProvidedByFileSystem, true, true, DateTimeOffset.UtcNow);
        var decision = new MediaMirrorApprovalDecision(request.RequestId, Approve: true);
        var health = new AgentHealth("Running", null, 1, Array.Empty<VolumeHealth>(), PendingMediaMirrorApprovals: [request]);

        var roundTripRequest = IpcProtocol.Read<PendingMediaMirrorApproval>(IpcProtocol.Create("PendingMediaMirrorApproval", request));
        var roundTripDecision = IpcProtocol.Read<MediaMirrorApprovalDecision>(IpcProtocol.Create("MediaMirrorApprovalDecision", decision));
        var roundTripHealth = IpcProtocol.Read<AgentHealth>(IpcProtocol.Create("AgentHealth", health));

        Assert.Equal(request, roundTripRequest);
        Assert.Equal(decision, roundTripDecision);
        Assert.Equal(MediaMirrorAclDisclosure.NotProvidedByFileSystem, Assert.Single(roundTripHealth.PendingMediaMirrorApprovals!).AclDisclosure);
    }
}
