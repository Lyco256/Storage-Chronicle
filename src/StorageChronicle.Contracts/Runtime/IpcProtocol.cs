using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StorageChronicle.Contracts.Runtime;

/// <summary>Major/minor version carried by every local IPC message.</summary>
public sealed record ProtocolVersion(int Major, int Minor);

/// <summary>JSON payload carried by the local named-pipe protocol.</summary>
public sealed record IpcEnvelope(ProtocolVersion Protocol, string MessageType, JsonElement Payload);

/// <summary>Defines the protocol version accepted by the current Agent.</summary>
public static class IpcProtocol
{
    /// <summary>Current protocol major version.</summary>
    public const int Major = 1;
    /// <summary>Current protocol minor version.</summary>
    public const int Minor = 0;

    /// <summary>Creates a source-generated envelope for one typed message.</summary>
    public static IpcEnvelope Create<T>(string messageType, T payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentNullException.ThrowIfNull(payload);
        return new IpcEnvelope(new ProtocolVersion(Major, Minor), messageType, JsonSerializer.SerializeToElement(payload, IpcJsonContext.Default.Options));
    }

    /// <summary>Reads the typed payload from an envelope.</summary>
    public static T Read<T>(IpcEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.Payload.Deserialize<T>(IpcJsonContext.Default.Options)
            ?? throw new InvalidDataException("IPC payload is empty.");
    }
}

/// <summary>System.Text.Json source-generated metadata for IPC envelopes.</summary>
[JsonSerializable(typeof(IpcEnvelope))]
[JsonSerializable(typeof(ProtocolVersion))]
[JsonSerializable(typeof(ProjectionPageRequest))]
[JsonSerializable(typeof(DiffProjectionRequest))]
[JsonSerializable(typeof(ProjectionPageResponse))]
[JsonSerializable(typeof(DiffProjectionResponse))]
[JsonSerializable(typeof(DiffProjectionItemSnapshot))]
[JsonSerializable(typeof(ReplayTimelinePointSnapshot))]
[JsonSerializable(typeof(AgentHealth))]
[JsonSerializable(typeof(AgentHealthRequest))]
[JsonSerializable(typeof(EventDetailsRequest))]
[JsonSerializable(typeof(EventDetailsResponse))]
[JsonSerializable(typeof(SettingsSnapshotRequest))]
[JsonSerializable(typeof(SettingsUpdateRequest))]
[JsonSerializable(typeof(SettingsSnapshot))]
[JsonSerializable(typeof(ClipboardCandidateRequest))]
[JsonSerializable(typeof(ReconciliationDecision))]
[JsonSerializable(typeof(PendingReconciliationRequest))]
[JsonSerializable(typeof(EventStackNodeSnapshot))]
[JsonSerializable(typeof(VolumeHealth))]
public partial class IpcJsonContext : JsonSerializerContext;

/// <summary>Implements the 4-byte little-endian length-prefixed local frame.</summary>
public sealed class LengthPrefixedJsonCodec : ILocalMessageCodec
{
    /// <summary>Maximum payload size enforced before allocation.</summary>
    public const int MaximumPayloadBytes = 8 * 1024 * 1024;

    /// <inheritdoc />
    public byte[] Encode<T>(T payload, int major, int minor)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, IpcJsonContext.Default.Options);
        if (json.Length > MaximumPayloadBytes) throw new InvalidDataException("IPC payload exceeds 8 MiB.");
        var frame = new byte[sizeof(int) + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame.AsSpan(sizeof(int)));
        return frame;
    }

    /// <inheritdoc />
    public T Decode<T>(ReadOnlySpan<byte> frame, int supportedMajor)
    {
        if (frame.Length < sizeof(int)) throw new InvalidDataException("IPC frame is truncated.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(frame);
        if (length < 0 || length > MaximumPayloadBytes || frame.Length - sizeof(int) != length) throw new InvalidDataException("IPC frame length is invalid.");
        var result = JsonSerializer.Deserialize<T>(frame.Slice(sizeof(int), length), IpcJsonContext.Default.Options);
        if (result is null) throw new InvalidDataException("IPC payload is empty.");
        if (result is IpcEnvelope envelope && envelope.Protocol.Major != supportedMajor) throw new InvalidDataException("IPC protocol major is unsupported.");
        return result;
    }
}
