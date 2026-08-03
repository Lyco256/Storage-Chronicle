# ADR-0001: Avalonia for the desktop UI

## Status

Accepted.

## Decision

Use Avalonia with MVVM for the desktop shell. UI interpretation remains in UI projects and does not leak into Domain, collection, normalization, state, or persistence modules.

## Rationale

Avalonia provides the required Windows desktop surface while keeping platform-neutral projections testable through headless tests.

## Consequences

UI projects carry the Avalonia dependency; ViewModels expose testable state and commands, while OS actions use explicit adapters.

## Verification

`tests/StorageChronicle.UI.Headless.Tests/` and `tests/StorageChronicle.UI.Settings.Tests/`.
