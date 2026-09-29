# StorageChronicle.UI.Desktop.csproj

## Role

Defines the Avalonia desktop composition root and references the platform-neutral Event Stack, Diff View, shared Agent IPC client, and settings dialog projects.

## Public types

The project compiles the public desktop shell and window types; it introduces no extra shared contracts.

## Invariants and dependencies

UI code depends on Agent IPC and projections, not on direct storage or collector APIs. The Settings UI references the canonical Settings contract and `StorageChronicle.UI.Settings`.

## Failure behavior and tests

Build fails on warnings and nullable warnings. `tests/StorageChronicle.UI.Headless.Tests` validates shell and modal construction; desktop build validates Avalonia compilation.
