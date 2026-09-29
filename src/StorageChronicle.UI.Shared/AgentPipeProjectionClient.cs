using System.Buffers.Binary;
using System.IO.Pipes;
using StorageChronicle.Contracts;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.UI.Shared;

/// <summary>Reads bounded projections from the local Agent named pipe.</summary>
public sealed class AgentPipeProjectionClient : IVirtualizedPageSource<EventStackRow>, IProjectionService
{
    private const int ConnectTimeoutMilliseconds = 2500;
    private readonly string pipeName;
    private readonly LengthPrefixedJsonCodec codec = new();

    /// <summary>Initializes a client for the local Agent pipe.</summary>
    public AgentPipeProjectionClient(string? pipeName = null) => this.pipeName = pipeName ?? "StorageChronicle.Agent";

    /// <summary>Loads a page using the requested mode and literal filter string.</summary>
    public async ValueTask<ProjectionPageResponse> GetPageAsync(ProjectionPageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendAsync("ProjectionPageRequest", request, cancellationToken).ConfigureAwait(false);
        return IpcProtocol.Read<ProjectionPageResponse>(response);
    }

    /// <inheritdoc />
    public async ValueTask<ProjectionPage<EventStackRow>> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var response = await GetPageAsync(new ProjectionPageRequest(EventStackMode.Grouped, page, pageSize, null), cancellationToken).ConfigureAwait(false);
        return new ProjectionPage<EventStackRow>(response.Items, response.Page, response.PageSize, response.TotalCount, response.HasMore);
    }

    /// <inheritdoc />
    public async ValueTask<ProjectionPage<EventStackRow>> GetEventStackAsync(EventStackMode mode, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var response = await GetPageAsync(new ProjectionPageRequest(mode, page, pageSize, null), cancellationToken).ConfigureAwait(false);
        return new ProjectionPage<EventStackRow>(response.Items, response.Page, response.PageSize, response.TotalCount, response.HasMore);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<DiffEntry>> GetDiffAsync(DateTimeOffset? fromUtc, DateTimeOffset toUtc, DiffMode mode, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("DiffProjectionRequest", new DiffProjectionRequest(fromUtc, toUtc, mode), cancellationToken).ConfigureAwait(false);
        return IpcProtocol.Read<DiffProjectionResponse>(response).Items;
    }

    /// <summary>Loads the rich, bounded Tree/Explorer diff payload from the Agent.</summary>
    public async ValueTask<DiffProjectionResponse> GetDiffProjectionSnapshotAsync(DiffProjectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendAsync("DiffProjectionRequest", request, cancellationToken).ConfigureAwait(false);
        return IpcProtocol.Read<DiffProjectionResponse>(response);
    }

    /// <summary>Loads detail data for one Event Stack selection.</summary>
    public async ValueTask<EventDetailsSnapshot?> GetDetailsAsync(EventId eventId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("EventDetailsRequest", new EventDetailsRequest(eventId), cancellationToken).ConfigureAwait(false);
        return IpcProtocol.Read<EventDetailsResponse>(response).Details;
    }

    /// <summary>Reads the non-user-editable Agent health and pending continuity decisions.</summary>
    public async ValueTask<AgentHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("AgentHealthRequest", new AgentHealthRequest(), cancellationToken).ConfigureAwait(false);
        return IpcProtocol.Read<AgentHealth>(response);
    }

    /// <summary>Submits one explicit reconciliation decision; the Agent performs the restart/recovery.</summary>
    public async ValueTask<AgentHealth> DecideReconciliationAsync(string requestId, bool execute, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        var response = await SendAsync("ReconciliationDecision", new ReconciliationDecision(requestId, execute), cancellationToken).ConfigureAwait(false);
        return IpcProtocol.Read<AgentHealth>(response);
    }

    private async ValueTask<IpcEnvelope> SendAsync<T>(string messageType, T request, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(ConnectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        var frame = codec.Encode(IpcProtocol.Create(messageType, request), IpcProtocol.Major, IpcProtocol.Minor);
        await pipe.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        var responseFrame = await ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
        var response = codec.Decode<IpcEnvelope>(responseFrame, IpcProtocol.Major);
        if (response.MessageType == "Error")
        {
            var error = IpcProtocol.Read<AgentHealth>(response);
            throw new IOException(error.Reason ?? "The Agent rejected the IPC request.");
        }

        return response;
    }

    private static async ValueTask<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > LengthPrefixedJsonCodec.MaximumPayloadBytes) throw new InvalidDataException("Agent IPC response length is invalid.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        var frame = new byte[sizeof(int) + length];
        header.CopyTo(frame, 0);
        payload.CopyTo(frame, sizeof(int));
        return frame;
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Agent IPC response ended early.");
            offset += read;
        }
    }
}
