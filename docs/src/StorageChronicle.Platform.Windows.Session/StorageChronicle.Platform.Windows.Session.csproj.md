# StorageChronicle.Platform.Windows.Session.csproj

## Role

Builds the Windows 10-compatible session/share adapter for `net10.0-windows10.0.19041.0`.

## Dependencies

References Domain, Contracts, and Platform.Abstractions only. Windows API calls remain inside this project. Nullable and warnings-as-errors settings come from the repository build properties.

## Tests

The corresponding test project supplies injected contract tests; privileged native tests remain separate from ordinary test execution.
