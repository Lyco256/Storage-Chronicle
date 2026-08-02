# External media handoff

- 担当要件: `Requirements/18_AGENT_EXTERNAL_MEDIA.md`
- 所有パス: `src/StorageChronicle.ExternalMedia/**`, `tests/StorageChronicle.ExternalMedia.Tests/**`
- 変更概要: immutable writer segments, record CRC32C, segment SHA-256, A/B manifests, mount sessions, branch detection, traversal checks.
- 既知の制限: import orchestration can be composed by Agent; this module does not mount or enumerate devices itself.
- 共有契約変更要求: none.
