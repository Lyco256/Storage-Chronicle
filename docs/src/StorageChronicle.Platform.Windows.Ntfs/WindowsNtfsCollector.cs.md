# WindowsNtfsCollector.cs

`WindowsNtfsCollector` adapts `IUsnJournalReader` to the shared `ISourceEventCollector` contract. It preserves LiveUsn versus RecoveredUsn, volume identity, parent/file identity, USN ordering, and quality. Rename old/new pairs become one correlated source event; unmatched rename observations remain with Unknown quality. Journal continuity, access, and media gaps become explicit `UnverifiedGap` source events, so one inaccessible volume does not stop the agent.

`IUsnJournalReader` remains the replaceable boundary for a future driver implementation. The collector depends on Domain, Contracts, and the local NTFS API types only; it never opens files or reads content.
