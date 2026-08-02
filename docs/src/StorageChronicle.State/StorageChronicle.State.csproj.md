# StorageChronicle.State.csproj

## Role

Defines the OS-neutral State project for the `net10.0` target and references the Domain project.

## Dependency and invariants

The project has no package, UI, Windows, or SQLite dependency. Shared solution registration is intentionally left to the top agent because the state-engine requirement forbids editing shared solution files.

## Tests and failure behavior

The project is built by the state-engine test project and inherits nullable, warnings-as-errors, deterministic, and analyzer settings from the repository build properties.
