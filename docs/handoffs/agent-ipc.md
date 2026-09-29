# Agent/IPC handoff

- 担当要件: `Requirements/17_AGENT_AGENT_SERVICE_IPC.md`
- 所有パス: `src/StorageChronicle.Application/**`, `src/StorageChronicle.Agent/**`, `src/StorageChronicle.Contracts/Runtime/**`
- 変更概要: bounded pipeline, versioned length-prefixed JSON protocol, authenticated named-pipe endpoints, settings gateway, and supervised durable failure recovery.
- 重要判断: source is appended before normalization; read-only observations are filtered by normalizer; queue capacity is explicit.
- 障害処理: collector、sink、normalization、SQLite、capacity の失敗は AgentHealth の品質状態へ記録し、Hosted Worker を終了させない。停止状態は `TryResumeAsync` の容量/保存境界を経て再開を試みる。
- 検証: `dotnet test tests/StorageChronicle.Agent.Tests/StorageChronicle.Agent.Tests.csproj --no-restore -c Debug` は13件合格。全非特権テストは `build/Test-Fast.ps1 -NoRestore` で合格。
- 既知の制限: named-pipe ACL and full collector dependency injection are deployment-layer work; the protocol boundary is platform-neutral. LocalSystem、Win10、物理媒体、SMB、サービス回復の実機受入は別マトリクスで実行する。
- 共有契約変更要求: none.

## Top-agent shared contract request: Diff View activity frames

Requirement 16.3 cannot be fulfilled by the current `DiffProjectionItemSnapshot`: the desktop needs process-instance identity, activity timing, operation breakdown, and event boundaries to render independent location panes and replay pane lifecycles. Add one backward-compatible optional `ActivityFrames` field to `DiffProjectionResponse`, with source-generated serializable `DiffActivityFrameSnapshot` and `DiffActivityFrameEventSnapshot` records in `src/StorageChronicle.Contracts/Runtime/RuntimeMessages.cs`. This request is owned and implemented by the top agent; do not create a competing frame DTO in Agent or desktop code.

The Agent should derive frame summaries from the existing `ActivityGrouper` over the same canonical event set as the diff response, and load `PaneTimeoutSeconds` from persisted User Settings. Frame event payloads are metadata only (event ID/time/path/operation/quality); they must never include file contents or content hashes. Keep the added response collection bounded and preserve existing clients that omit it. Add IPC source-generation/round-trip coverage and Agent projection tests for frame metrics, concurrent locations, timeout boundary, cancellation, and settings fallback. No physical environment is required.
