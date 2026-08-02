# WindowsExternalMediaMonitor

Wraps the native Configuration Manager notification callback in an asynchronous channel. Connected/disconnected notifications are event-driven and do not poll or enumerate a volume every second. Disposal unregisters the callback and completes the channel. Tests use a callback fake; privileged OS notification tests belong in the Windows integration category.
