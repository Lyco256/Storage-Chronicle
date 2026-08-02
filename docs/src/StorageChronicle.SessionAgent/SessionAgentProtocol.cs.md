# SessionAgentProtocol.cs

## Role

Defines the versioned session-agent clipboard DTO, envelope, frame codec, and typed protocol error.

## Wire format

Each frame is a 4-byte little-endian signed payload length followed by UTF-8 JSON containing major, minor, message type, and payload. Payloads are limited to 1 MiB and unsupported majors are rejected before deserialization is accepted.

## Failure behavior

Invalid lengths, empty message types, malformed JSON, missing payloads, and unsupported protocol majors throw `SessionAgentProtocolException`. The codec contains no OS or pipe dependency and is deterministic.

## Tests

Owned tests cover round-trip fields, major mismatch, corrupt length, and the server's delivery/disconnect behavior.
