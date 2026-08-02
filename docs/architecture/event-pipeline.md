# Event pipeline

Collectors emit bounded source envelopes. Normalization deterministically produces canonical operations while preserving origin, sequence, quality, and process attribution. Source and canonical facts are persisted before state application. Read/open/query observations stay in bounded transient correlation buffers and are never durable history.
