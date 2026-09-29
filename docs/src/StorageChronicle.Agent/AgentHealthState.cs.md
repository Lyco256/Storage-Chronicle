# AgentHealthState

Aggregates collector failures, recording status, and per-volume continuity for the read-only Agent health IPC response. It stores only volume identity, continuity, and a bounded reason; it never stores file names or content. It is fed by `AgentPipeline.SourceObserved` and `CollectorFailed`.
