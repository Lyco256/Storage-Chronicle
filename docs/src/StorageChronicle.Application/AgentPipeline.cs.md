# AgentPipeline.cs

Coordinates bounded source storage, deterministic normalization, canonical storage, and state application. Backpressure uses a bounded channel; source facts are stored before canonicalization, and cancellation completes the channel without unbounded memory.
