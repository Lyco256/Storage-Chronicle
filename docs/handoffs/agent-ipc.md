# Agent/IPC handoff

- 担当要件: `Requirements/17_AGENT_AGENT_SERVICE_IPC.md`
- 所有パス: `src/StorageChronicle.Application/**`, `src/StorageChronicle.Agent/**`, `src/StorageChronicle.Contracts/Runtime/**`
- 変更概要: bounded pipeline and versioned length-prefixed JSON protocol.
- 重要判断: source is appended before normalization; read-only observations are filtered by normalizer; queue capacity is explicit.
- 既知の制限: named-pipe ACL and full collector dependency injection are deployment-layer work; the protocol boundary is platform-neutral.
- 共有契約変更要求: none.
