# WindowsSourceEventFactory

Converts initial metadata and continuity failures into the existing `SourceEvent` contract. Initial entries carry standard metadata only, while gaps carry an explicit reason and `UnverifiedGap` quality. The factory creates no content hashes, process attribution, or synthetic descendant events.
