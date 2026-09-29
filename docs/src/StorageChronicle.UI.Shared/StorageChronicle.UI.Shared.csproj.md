# StorageChronicle.UI.Shared.csproj

## Role

Defines platform-neutral contracts and shared Agent IPC client code used by the desktop feature views.

## Public types and responsibilities

The project compiles `IFeatureView`, `IUserSettingsClient`, bounded page contracts, semantic icon contracts, and the named-pipe projection/settings client.

## Invariants

UI consumers use the Agent boundary for settings and projections; they do not access history or settings files directly.

## Dependencies

References Domain, Contracts, Settings, Avalonia, and CommunityToolkit.Mvvm. It does not reference Storage or Windows collector projects.

## Failure behavior

IPC and deserialization failures are returned to the async UI caller; no synthetic projection or settings snapshot is substituted.

## Relevant tests

Covered by the UI headless and Agent IPC contract tests.

## OS constraints

UI contracts are platform-neutral; the concrete named-pipe transport is Windows-specific at runtime.
