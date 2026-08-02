using StorageChronicle.Contracts;
using StorageChronicle.Platform.Abstractions;

namespace StorageChronicle.SessionAgent;

/// <summary>Result of one session-agent pipe lifetime.</summary>
public sealed record SessionAgentPipeRunResult(int SentMessages, bool Disconnected);

/// <summary>Streams clipboard candidates to an Agent without reading clipboard state from Session 0.</summary>
public sealed class SessionAgentPipeServer
{
    private readonly IClipboardEventSource source;

    /// <summary>Creates a pipe server over a user-session clipboard source.</summary>
    public SessionAgentPipeServer(IClipboardEventSource source) => this.source = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>Writes versioned frames until cancellation, source completion, or client disconnect.</summary>
    public async Task<SessionAgentPipeRunResult> RunAsync(Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var sent = 0;
        try
        {
            await foreach (var value in source.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = SessionAgentMessageCodec.Encode("ClipboardCandidate", ClipboardCandidateMessage.FromSourceEvent(value));
                await output.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                sent++;
            }

            return new SessionAgentPipeRunResult(sent, false);
        }
        catch (IOException)
        {
            return new SessionAgentPipeRunResult(sent, true);
        }
        catch (ObjectDisposedException)
        {
            return new SessionAgentPipeRunResult(sent, true);
        }
    }
}
