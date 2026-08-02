# System overview

Storage Chronicle separates source acquisition, canonical event recording, normalization, state reconstruction, projection, persistence, IPC, and UI. The durable source and canonical event streams are append-only; SQLite and UI projections are rebuildable.

The Windows service owns collection and persistence. The session agent supplies clipboard and session-scoped observations. The Avalonia desktop shell consumes platform-neutral projections and never reads the file system directly.
