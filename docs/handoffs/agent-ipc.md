# Agent/IPC handoff

- 担当要件: `Requirements/17_AGENT_AGENT_SERVICE_IPC.md`
- 所有パス: `src/StorageChronicle.Application/**`, `src/StorageChronicle.Agent/**`, `src/StorageChronicle.Contracts/Runtime/**`
- 変更概要: bounded pipeline, versioned length-prefixed JSON protocol, authenticated named-pipe endpoints, settings gateway, and supervised durable failure recovery.
- 重要判断: source is appended before normalization; read-only observations are filtered by normalizer; queue capacity is explicit.
- 障害処理: collector、sink、normalization、SQLite、capacity の失敗は AgentHealth の品質状態へ記録し、Hosted Worker を終了させない。停止状態は `TryResumeAsync` の容量/保存境界を経て再開を試みる。
- 検証: `dotnet test tests/StorageChronicle.Agent.Tests/StorageChronicle.Agent.Tests.csproj --no-restore -c Debug` は13件合格。全非特権テストは `build/Test-Fast.ps1 -NoRestore` で合格。
- 既知の制限: named-pipe ACL and full collector dependency injection are deployment-layer work; the protocol boundary is platform-neutral. LocalSystem、Win10、物理媒体、SMB、サービス回復の実機受入は別マトリクスで実行する。
- 共有契約変更要求: none.
