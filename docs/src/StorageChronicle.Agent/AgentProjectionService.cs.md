# AgentProjectionService

Adapts durable Agent history to the platform-neutral Projection service. Source and normalized Event Stack pages use SQLite-bounded reads and counts; grouped mode regenerates complete groups to keep group boundaries atomic. Diff requests load only the requested time range in bounded batches. It never reads file contents or content hashes.
