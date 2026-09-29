# StorageChronicle.UI.EventStack.csproj

## Role

Defines the Avalonia Event Stack feature view and its MVVM projection adapter.

## Public types and responsibilities

The project compiles the Event Stack view, UI contracts, material operation-icon resolver, and view models.

## Invariants

The feature uses only the projection and user-settings IPC interfaces. It does not depend on Storage or Windows collector APIs and materializes only the selected page.

## Dependencies

References Domain, Contracts, Settings, UI.Shared, Avalonia, CommunityToolkit.Mvvm, and Material.Icons.Avalonia.

## Failure behavior

Projection, cancellation, Agent settings, and binding errors remain visible at the feature boundary and do not fabricate event history.

## Relevant tests

Covered by `tests/StorageChronicle.UI.EventStack.Tests`, including Avalonia Headless tests.

## OS constraints

The feature is platform-neutral; Agent communication is supplied by the host's platform-specific client.
