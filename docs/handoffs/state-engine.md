# State engine handoff

- 担当要件: `Requirements/10_AGENT_STATE_ENGINE.md`
- 所有パス: `src/StorageChronicle.State/**`, `tests/StorageChronicle.State.Tests/**`
- 変更概要: immutable-in-read snapshots, relationship revisions, ancestor path reconstruction, idempotence and ordering checks.
- テスト: `dotnet test tests/StorageChronicle.State.Tests/StorageChronicle.State.Tests.csproj`
- 既知の制限: the current in-memory implementation is the state port implementation; Storage integration can replay it from append segments.
- 共有契約変更要求: none.
