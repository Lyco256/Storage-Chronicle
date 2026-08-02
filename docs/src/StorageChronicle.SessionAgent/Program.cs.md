# Program.cs

## Role

Defines the user-session process lifetime and optional named-pipe hosting loop.

## Lifecycle

Without `--pipe`, the process waits for controlled shutdown. With `--pipe`, it creates a Windows named-pipe server, waits for an Agent connection, streams clipboard candidates through `SessionAgentPipeServer`, and creates a fresh pipe after disconnect. Ctrl+C cancels the lifetime.

## Boundary and failure behavior

The process constructs `WindowsClipboardNotificationSource` and `WindowsClipboardReader` inside the logged-on session. It never runs clipboard access from the Windows service boundary. Cancellation disposes the listener and pipe; disconnects are recoverable.

## Tests

The protocol/server tests exercise the hosting components through injected clipboard streams and memory/disconnecting streams; actual user clipboard access is reserved for privileged smoke tests.
