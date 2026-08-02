using System.Buffers.Binary;
using System.Text.Json;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.SessionAgent;

/// <summary>A versioned clipboard candidate sent from the user-session process.</summary>
public sealed record ClipboardCandidateMessage(long Generation, IReadOnlyList<string> Paths, bool IsCut, string Quality, DateTimeOffset ObservedUtc)
{
    /// <summary>Creates a bounded IPC payload from a clipboard SourceEvent.</summary>
    public static ClipboardCandidateMessage FromSourceEvent(SourceEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var paths = value.Properties
            .Where(item => item.Key.StartsWith("clipboardPath.", StringComparison.Ordinal))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Value)
            .ToArray();
        var generation = value.Properties.TryGetValue("clipboardGeneration", out var generationText) && long.TryParse(generationText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedGeneration) ? parsedGeneration : 0;
        var isCut = value.Properties.TryGetValue("clipboardIsCut", out var isCutText) && bool.TryParse(isCutText, out var parsedIsCut) && parsedIsCut;
        var quality = value.Properties.TryGetValue("clipboardQuality", out var qualityText) ? qualityText : "NotIdentified";
        return new ClipboardCandidateMessage(generation, paths, isCut, quality, value.Time.RecordedUtc);
    }
}

/// <summary>Decoded versioned session-agent message.</summary>
public sealed record SessionAgentMessage<T>(int Major, int Minor, string MessageType, T Payload);

/// <summary>Rejects malformed, oversized, or incompatible session-agent frames.</summary>
public sealed class SessionAgentProtocolException : Exception
{
    /// <summary>Creates a protocol exception.</summary>
    public SessionAgentProtocolException(string message) : base(message) { }

    /// <summary>Creates a protocol exception with the malformed-input cause.</summary>
    public SessionAgentProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Encodes and decodes 4-byte little-endian length-prefixed JSON frames.</summary>
public static class SessionAgentMessageCodec
{
    /// <summary>Current protocol major.</summary>
    public const int CurrentMajor = 1;

    /// <summary>Current protocol minor.</summary>
    public const int CurrentMinor = 0;

    /// <summary>Maximum encoded JSON payload, excluding the length prefix.</summary>
    public const int MaxPayloadBytes = 1024 * 1024;

    /// <summary>Encodes a typed session-agent message.</summary>
    public static byte[] Encode<T>(string messageType, T payload, int major = CurrentMajor, int minor = CurrentMinor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        if (major < 0 || minor < 0) throw new ArgumentOutOfRangeException(nameof(major));
        var envelope = new SessionAgentEnvelope(major, minor, messageType, JsonSerializer.SerializeToElement(payload));
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope);
        if (json.Length > MaxPayloadBytes)
        {
            throw new SessionAgentProtocolException("The session-agent message exceeds the maximum frame size.");
        }

        var frame = new byte[sizeof(int) + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, sizeof(int)), json.Length);
        json.CopyTo(frame.AsSpan(sizeof(int)));
        return frame;
    }

    /// <summary>Decodes one complete frame and rejects unsupported protocol majors.</summary>
    public static SessionAgentMessage<T> Decode<T>(ReadOnlySpan<byte> frame, int supportedMajor = CurrentMajor)
    {
        if (frame.Length < sizeof(int)) throw new SessionAgentProtocolException("The session-agent frame has no length prefix.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(frame[..sizeof(int)]);
        if (length < 1 || length > MaxPayloadBytes || frame.Length != sizeof(int) + length) throw new SessionAgentProtocolException("The session-agent frame length is invalid.");
        SessionAgentEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SessionAgentEnvelope>(frame.Slice(sizeof(int), length)) ?? throw new SessionAgentProtocolException("The session-agent frame is empty.");
        }
        catch (JsonException exception)
        {
            throw new SessionAgentProtocolException("The session-agent frame contains invalid JSON.", exception);
        }
        if (envelope.Major != supportedMajor) throw new SessionAgentProtocolException($"Unsupported session-agent protocol major {envelope.Major}.");
        if (string.IsNullOrWhiteSpace(envelope.MessageType)) throw new SessionAgentProtocolException("The session-agent message type is missing.");
        T? payload;
        try
        {
            payload = envelope.Payload.Deserialize<T>();
        }
        catch (JsonException exception)
        {
            throw new SessionAgentProtocolException("The session-agent payload is malformed.", exception);
        }

        if (payload is null) throw new SessionAgentProtocolException("The session-agent payload is missing.");
        return new SessionAgentMessage<T>(envelope.Major, envelope.Minor, envelope.MessageType, payload);
    }

    private sealed record SessionAgentEnvelope(int Major, int Minor, string MessageType, JsonElement Payload);
}
