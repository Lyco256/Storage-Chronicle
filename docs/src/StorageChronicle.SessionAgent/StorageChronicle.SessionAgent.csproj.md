# StorageChronicle.SessionAgent.csproj

## Role

Builds the user-session executable for Windows 10-compatible .NET 10.

## Dependencies

References only the owned Windows session adapter, which in turn references the shared OS-neutral contracts. Named-pipe and user-session APIs stay in the process boundary.

## Tests

The corresponding session test project references this executable for protocol/server contract tests without starting the production lifetime.
