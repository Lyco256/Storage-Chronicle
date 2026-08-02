# AgentPipeProjectionClient.cs

This client is the UI's only Agent boundary. It sends bounded length-prefixed requests for Event Stack, rich Diff View, details, health, settings, and explicit reconciliation decisions, rejects oversized/error frames, and never performs direct history or filesystem I/O. Health responses carry per-volume continuity and one-time-presented pending gap decisions; the decision call delegates recovery to the Agent lifecycle.
