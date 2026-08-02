# AgentWorker.cs

Adapts the multi-collector pipeline to the .NET hosted-service lifecycle. It forwards collector failures, source continuity, and bounded queue depth to health IPC, disposes event-driven Windows sources on shutdown, and flushes durable storage before service completion. It has no UI and is intended for LocalSystem deployment with service recovery settings.
