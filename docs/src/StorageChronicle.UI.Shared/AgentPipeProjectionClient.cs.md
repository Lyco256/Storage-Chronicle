# AgentPipeProjectionClient.cs

In addition to bounded projections, this single client implements the canonical settings gateway over `SettingsSnapshotRequest` and scoped `SettingsUpdateRequest` messages. Machine/User settings and recovery warnings are returned from one Agent snapshot; machine writes remain Agent-authorized and UI code has no direct settings-store dependency.

This client is the UI's only Agent boundary. It sends bounded length-prefixed requests for Event Stack, rich Diff View, details, health, user settings, and explicit reconciliation decisions, rejects oversized/error frames, and never performs direct history or settings-file I/O. User-settings writes use the Agent's validated User scope. Health responses carry per-volume continuity and one-time-presented pending gap decisions; the decision call delegates recovery to the Agent lifecycle.
