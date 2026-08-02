# WindowsShareStateSource.cs

## Role

Implements the shared SMB share source: startup snapshot plus event-driven change snapshots and deterministic diffs.

## Inputs and outputs

`IShareSnapshotReader` supplies local `ShareDescriptor` values. `IShareChangeNotifier` waits for LanmanServer registry changes. The source emits `SourceEvent` values with `ShareChange` origin, `ShareChanged` hint, share properties, and a quality marker of `RegistryNotification` or `FallbackPolling`.

## Invariants and failure behavior

The initial snapshot is read once. Registry notification is preferred; when registration is unavailable or fails, the source waits 30 seconds between `NetShareEnum` snapshots. Share changes remain separate from file metadata and contain no remote-user or remote-PC data. Cancellation interrupts waits and snapshot reads.

## Tests

Owned tests inject snapshot/notifier fakes to verify path, description, permission changes, dedicated share origin, exact notification quality, and fallback quality without changing the shared contract.
