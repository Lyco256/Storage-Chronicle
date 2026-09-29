# Agent/IPC handoff

- 担当要件: `Requirements/17_AGENT_AGENT_SERVICE_IPC.md`
- 所有パス: `src/StorageChronicle.Application/**`, `src/StorageChronicle.Agent/**`, `src/StorageChronicle.Contracts/Runtime/**`
- 変更概要: bounded pipeline, versioned length-prefixed JSON protocol, authenticated named-pipe endpoints, settings gateway, and supervised durable failure recovery.
- 重要判断: source is appended before normalization; read-only observations are filtered by normalizer; queue capacity is explicit.
- 障害処理: collector、sink、normalization、SQLite、capacity の失敗は AgentHealth の品質状態へ記録し、Hosted Worker を終了させない。停止状態は `TryResumeAsync` の容量/保存境界を経て再開を試みる。
- 検証: `dotnet test tests/StorageChronicle.Agent.Tests/StorageChronicle.Agent.Tests.csproj --no-restore -c Debug` は13件合格。全非特権テストは `build/Test-Fast.ps1 -NoRestore` で合格。
- 既知の制限: named-pipe ACL and full collector dependency injection are deployment-layer work; the protocol boundary is platform-neutral. LocalSystem、Win10、物理媒体、SMB、サービス回復の実機受入は別マトリクスで実行する。
- 共有契約変更要求: none.

## Top-agent integration: Activity Frame service contract

`DiffProjectionResponse` now carries a separately paged optional Activity Frame summary list; selected-frame canonical events use the independent `DiffActivityFrameTimelineRequest/Response`. Agent summaries come from the canonical log through `ActivityGrouper`, use the persisted `PaneTimeoutSeconds` (5-second product default when no settings store is supplied), and are exposed for Live/Replay only. Summary sort direction is explicit in the backwards-compatible request and applied before paging, so newest-first remains correct even when the history exceeds one page. Each event detail contains event id/time/path/operation/quality only, never contents or content hashes. Page size is validated and bounded; malformed ranges, missing frame IDs, and cancellation remain explicit.

Added coverage checks source-generated IPC round trips, concurrent route aggregation, measured timeout/expiry boundaries, independent summary/event paging, settings fallback, non-Live omission, and cancellation. Commands run on the integration worktree: `dotnet build tests/StorageChronicle.Agent.Tests/StorageChronicle.Agent.Tests.csproj --no-restore --nologo` (0 warnings/errors); `StorageChronicle.Agent.Tests.exe --filter-class StorageChronicle.Agent.Tests.AgentProjectionServiceTests --progress off --minimum-expected-tests 1` (3/3 passed). Physical acceptance was not run.

Top-agent follow-up adds `IProcessLifecycleSink` as a platform-neutral transient boundary. Windows ETW ProcessStop is matched only to the cached Process Instance ID, reported to the bounded in-memory Agent registry, and used to shorten a matching Live Frame's close boundary; it is never appended as a file/source/canonical event. Missing or unknown PID mapping is ignored, not guessed. Service restart clears this observation, so historical Replay cannot claim a process-exit boundary that was not retained in product history; this restart/replay behavior remains an explicit lifecycle limitation for acceptance review.

Top-agent verification after server-side pre-page sorting and a legacy request compatibility case: `dotnet test tests/StorageChronicle.Agent.Tests/StorageChronicle.Agent.Tests.csproj --no-restore` passed 46 tests; the 3 physical tests were expectedly skipped because no approved isolated acceptance root/elevated lane is configured. `./build/Test-All.ps1` exited 0 on the same implementation before this test-only compatibility addition. Legacy Diff requests missing Activity Frame paging/order fields deserialize to defaults (page 1, size 100, newest-first).
