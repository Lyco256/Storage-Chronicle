# Program.cs

## Role

Starts the logged-on Session Agent, creates the hidden event-driven clipboard listener, and forwards only bounded clipboard metadata to the Agent's versioned named pipe.

## Boundary and failure behavior

The Session Agent never transports clipboard bytes. It maps path candidates, generation, quality, and source sequence to `ClipboardCandidateRequest`, reconnects per bounded message, reads the response frame, and honors cancellation.

## Tests

Protocol and clipboard source tests cover malformed frames, cancellation, clipboard lock retry, disconnect, and source mapping. Real clipboard smoke tests remain in the privileged Windows category.
