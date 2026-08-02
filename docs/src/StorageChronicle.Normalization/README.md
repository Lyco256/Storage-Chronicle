# Normalization

This module owns deterministic SourceEvent-to-CanonicalEvent conversion. It depends only on Domain and Contracts; it has no Windows, UI, storage, or file-content dependency.

Read-only ETW observations and Clipboard intent are transient. Canonical events preserve source quality and identity, while bounded correlation adds only confirmed Clipboard relationships. The complete implementation and invariants are documented in `EventNormalizer.cs.md`.
