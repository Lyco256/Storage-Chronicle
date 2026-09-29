# CanonicalEventSinks.cs

`ICanonicalEventSink` is the extension point for secondary durable consumers of canonical events. The primary append-only log and state store remain authoritative; sinks are invoked only after those writes succeed and expose bounded flush/stop hooks for supervised Agent restarts.

The interface carries canonical metadata only and cannot receive file contents or content hashes. Implementations must isolate secondary I/O failures, preserve ordering within their scope, and keep buffers bounded.
