# AgentProcessLifecycleState.cs

Stores bounded observed process-instance exit times in memory and, when wired to the product storage engine, asynchronously durably records exact process-exit facts for Activity Frame Replay without persisting a synthetic file event.

## Role

Implements the bounded in-memory lifecycle registry shared by the Windows collector and Agent projection service.

## Public types and responsibilities

`AgentProcessLifecycleState` implements `IProcessLifecycleSink` and `IAsyncDisposable`. It accepts uniquely identified process instances, keeps the earliest duplicate exit observation in its fixed-capacity (8192) transient cache, and queues a distinct durable `ProcessLifecycleEvent` for a bounded single-reader writer when storage is provided.

## Inputs and outputs

Input is a `ProcessInstanceId` and observed `DateTimeOffset`; lookup returns a matching timestamp when present. With production DI, an observed earlier exit is queued to product-owned append-only history without blocking the ETW callback; async disposal drains the queue. It does not write source/canonical file events, settings, or operating-system configuration.

## Dependencies

Depends on `IProcessLifecycleSink` and the domain `ProcessInstanceId` contract.

## Invariants

Exit observations are never appended as Source or Canonical file events. Unknown or invalid identity/time is rejected rather than guessed. The in-memory cache clears on Agent restart; historical Replay obtains durable facts from the separate lifecycle index and segment records.

## Threading and lifetime

Concurrent dictionary and queue permit collector writes and projection reads. Lifetime is the Agent process; capacity is bounded.

## Failure behavior

Invalid identity or default timestamp throws `ArgumentException`. Concurrent updates preserve the earliest observed exit; missing keys return false. Queue saturation fails visibly to the collector, and asynchronous persistence failure is reported to `AgentHealthState` and prevents later records from being accepted as durable.

## Tests

`tests/StorageChronicle.Agent.Tests/AgentProjectionServiceTests.cs` covers projection effects, restart Replay recovery, Replay range-start overlap, invalid observations, earliest duplicate handling, bounded eviction, persistence failure reporting, and graceful queue drain.

## OS constraints

Platform-neutral in-memory implementation; Windows ETW acquisition is isolated in the Windows NTFS platform project.

## Change-sensitive contracts

Capacity and duplicate-observation semantics are behavior-sensitive contracts. Lifecycle facts remain separate from file-event contracts.
