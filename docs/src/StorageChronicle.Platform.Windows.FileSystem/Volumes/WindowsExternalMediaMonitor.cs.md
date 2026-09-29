# WindowsExternalMediaMonitor

Wraps the native Configuration Manager notification callback in an asynchronous channel. Connected/disconnected notifications are event-driven and do not poll or enumerate a volume every second. If native registration is unavailable, the monitor remains disposable and exposes the failure so the Agent can record an `UnverifiedGap` without stopping other collectors. Disposal unregisters the callback and completes the channel. Tests use callback and failure fakes; privileged OS notification tests belong in the Windows integration category.
