# ADR 002: Append-only log plus rebuildable SQLite

The immutable log preserves source quality and supports recovery. SQLite accelerates state, search, and projections but is never the sole source of truth.
