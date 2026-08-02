# WindowsVolumeSnapshotReader

Performs directory-unit initial enumeration with bounded result batches. It records link/junction/reparse objects themselves and does not push reparse directories onto the traversal stack. Access-denied metadata falls back to `ExistenceOnly`; directory disappearance and enumeration I/O failures become gap events. Exclusions are evaluated before metadata acquisition. Tests cover reparse non-recursion, access fallback, cancellation, FAT/exFAT-style quality, and no content access.
