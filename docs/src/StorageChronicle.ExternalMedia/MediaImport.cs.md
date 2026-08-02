# MediaImport.cs

Implements confirmed-history import from every writer directory on a media root and persists the PC-side deduplication ledger with an atomic temporary-file replacement. Imports validate manifests and segments through `ExternalMediaStore`, skip already-known segment hashes, detect multiple writer PCs, report unavailable parents/corrupt segments, and apply the media-only filter before returning events.

The importer does not rewrite media history or infer missing events. A corrupt or missing segment produces an honest warning and `UnverifiedGap`; cancellation propagates from directory enumeration and file reads. Tests cover PC-A to PC-B handoff, same-event/segment deduplication, branch inputs, and ledger reload.
