# IProcessLifecycleSink.cs

Defines the platform-neutral transient contract for reporting process-instance exit observations to Agent projections without converting them into durable file-history events.

## Role

Provides a narrow boundary between platform process lifecycle acquisition and Activity Frame lifecycle projection.

## Public types and responsibilities

`IProcessLifecycleSink` records an observed exit time by unique `ProcessInstanceId` and allows lookup while the owning Agent remains alive. PID-only correlation is not part of this API.

## Inputs and outputs

Methods accept a process instance identity and timestamp or return whether an observation exists. The contract contains no file contents, file-content hashes, or filesystem mutation operations.

## Dependencies

Depends only on the domain `ProcessInstanceId` value type.

## Invariants

Callers must supply the instance identity associated with an observed process start. Missing observations remain missing; implementations must not infer process termination from absent activity. This contract does not require durable storage.

## Threading and lifetime

Implementations must support concurrent collector updates and projection lookups for the Agent lifetime.

## Failure behavior

Implementations may reject invalid identity or timestamps. Unknown instances return false and do not synthesize an exit observation.

## Tests

`tests/StorageChronicle.Agent.Tests/AgentProjectionServiceTests.cs` exercises process-exit close boundaries and the bounded registry implementation.

## OS constraints

Platform-neutral contract; Windows ETW and future platform lifecycle sources remain isolated in their platform projects.

## Change-sensitive contracts

The unique process-instance key, transient lifetime, and absence of durable file-event semantics are compatibility-sensitive.
