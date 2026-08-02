# SessionAgentPipeServer.cs

## Role

Streams `IClipboardEventSource` values to an output stream as versioned `ClipboardCandidate` frames.

## Inputs and outputs

The server consumes Domain `SourceEvent` values and maps bounded clipboard properties into `ClipboardCandidateMessage`. It writes one length-prefixed frame per event, flushes it, and reports sent count plus disconnect status.

## Failure, cancellation, and recovery

Cancellation is propagated. IOException and ObjectDisposedException from a disconnected client produce `Disconnected=true` so the host can reopen a named pipe. The server does not persist, normalize, or confirm copy operations.

## Tests

The owned tests use a memory stream for successful framing and a disconnecting stream for recovery behavior.
