# Handoff: Agent process-lifecycle persistence retry

Branch: `feat/agent-lifecycle-retry`

## Changes

- Preserve the in-flight ProcessStop event and the queued tail in a fixed-capacity retry queue when lifecycle persistence fails; expose the retained count and underlying failure in Agent health.
- Retain later ProcessStop observations in that same bounded queue while storage remains stopped. If it is full, fail visibly instead of claiming the newest fact is durable.
- Add `AgentProcessLifecycleState.RetryPendingAsync`, which retries in order with the original EventIds and leaves all remaining facts queued after cancellation or another persistence failure. After draining, it starts a fresh bounded callback queue.
- Connect the Agent worker's storage recovery path to lifecycle retry.
- Add capacity-stop, cancellation, repeated-failure, successful recovery, and post-recovery-write tests. Update source mirrors.

## Validation

- `./build/Test-All.ps1`: passed; solution build had zero warnings/errors; all enabled test, coverage, architecture, integration, and UI checks passed. Agent tests: 52 passed, 3 physical/elevated acceptance tests skipped by their guards.
- `./build/quality/Test-DocMirror.ps1`: passed.
- `git diff --check`: passed.
- No service, installer, elevated test, physical acceptance, VHDX, SMB, or product execution was performed.

## Known limitations

- The retry queue is intentionally bounded and in-memory. Abrupt process termination can still lose events that have not reached durable storage; power-loss injection remains pending.
- Physical acceptance and final-commit-bound read-only/source-to-sink audit remain unexecuted. This handoff does not authorize running the product on a host.
- Requirements documents were not modified.
