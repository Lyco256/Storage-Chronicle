# AgentPipeline.cs

Coordinates multiple collectors through bounded source storage, deterministic normalization, canonical storage, and state application. Backpressure uses a bounded channel and reports its depth to the host health surface; source facts are stored before canonicalization, collector failures are isolated and reported, and cancellation completes the channel without unbounded memory.
