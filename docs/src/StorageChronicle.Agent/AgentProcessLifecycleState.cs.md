# AgentProcessLifecycleState.cs

Stores observed process-instance exit times only for the current Agent lifetime so Activity Frame projections can use an observed process-exit boundary without persisting a synthetic file event.

## Role

Implements the bounded in-memory lifecycle registry shared by the Windows collector and Agent projection service.

## Public types and responsibilities

`AgentProcessLifecycleState` implements `IProcessLifecycleSink`. It accepts uniquely identified process instances, keeps the earliest duplicate exit observation, and evicts old entries above its fixed capacity of 8192.

## Inputs and outputs

Input is a `ProcessInstanceId` and observed `DateTimeOffset`; lookup returns the matching timestamp when present. It performs no file, history, settings, or operating-system writes.

## Dependencies

Depends on `IProcessLifecycleSink` and the domain `ProcessInstanceId` contract.

## Invariants

Exit observations are transient and are never appended as Source or Canonical file events. Unknown or invalid identity/time is rejected rather than guessed. Agent restart clears the registry, so prior process exits are not asserted during Replay.

## Threading and lifetime

Concurrent dictionary and queue permit collector writes and projection reads. Lifetime is the Agent process; capacity is bounded.

## Failure behavior

Invalid identity or default timestamp throws `ArgumentException`. Concurrent updates preserve the earliest observed exit; missing keys return false.

## Tests

`tests/StorageChronicle.Agent.Tests/AgentProjectionServiceTests.cs` covers projection effects, invalid observations, earliest duplicate handling, and bounded eviction.

## OS constraints

Platform-neutral in-memory implementation; Windows ETW acquisition is isolated in the Windows NTFS platform project.

## Change-sensitive contracts

Capacity, duplicate-observation semantics, and the non-durable lifecycle boundary are behavior-sensitive contracts.
