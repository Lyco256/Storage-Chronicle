# State and storage model

File objects and parent/name directory-entry versions are separate. A folder move changes one relationship; descendants are reconstructed through the ancestor chain at a requested time. Append-only segments are authoritative, while SQLite stores indexes, current state, and projection cache and can be rebuilt after loss.
