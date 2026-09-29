# WindowsCloudPlaceholderReader.cs

## Role

Reads local `FileAttributes` flags and maps them to Hydrated, Dehydrated, or SourceUnknown placeholder observations.

## Boundary and invariants

The reader is local-only. It does not invoke a cloud API, retrieve cloud history, or access user identity. Offline, unpinned, and recall-on-data flags indicate Dehydrated; ordinary local attributes indicate Hydrated. A separate classifier marks metadata-only changes when the state is unchanged.

## Failure and tests

Unsupported platforms, missing paths, access denied, and I/O failures return bounded Unknown/Unsupported observations with quality `Unknown`. Tests cover capability denial, missing paths, hydration transitions, and metadata-only changes.
