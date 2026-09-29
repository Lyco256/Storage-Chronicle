# StorageChronicle.SessionAgent

## Role

Runs in the logged-on user session, owns the clipboard listener boundary, and streams bounded clipboard candidates to the Agent over a versioned local pipe.

## Public contract

`SessionAgentMessageCodec` frames UTF-8 JSON with a 4-byte little-endian length and protocol major/minor. `SessionAgentPipeServer` streams `ClipboardCandidateMessage` values and returns a disconnect result so the host can reconnect.

## Invariants

The process does not read clipboard state from Session 0. The protocol rejects unsupported majors, malformed lengths, oversized frames, invalid JSON, and missing payloads. Candidate payloads contain paths, generation, copy/cut, quality, and observation time only.

## Failure and recovery

Client stream disconnects become `Disconnected=true` rather than corrupting durable history; `Program` creates a new named-pipe server for the next client. Cancellation propagates for controlled shutdown.

## Tests

The owned session tests cover protocol round trips, major-version rejection, corrupt lengths, stream delivery, and disconnect recovery.
