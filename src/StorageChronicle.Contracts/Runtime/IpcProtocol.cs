using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StorageChronicle.Contracts.Runtime;

/// <summary>Major/minor version carried by every local IPC message.</summary>
public sealed record ProtocolVersion(int Major, int Minor);

/// <summary>JSON payload carried by the local named-pipe protocol.</summary>
public sealed record IpcEnvelope(ProtocolVersion Protocol, string MessageType, JsonElement Payload);

/// <summary>System.Text.Json source-generated metadata for IPC envelopes.</summary>
[JsonSerializable(typeof(IpcEnvelope))]
[JsonSerializable(typeof(ProtocolVersion))]
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
