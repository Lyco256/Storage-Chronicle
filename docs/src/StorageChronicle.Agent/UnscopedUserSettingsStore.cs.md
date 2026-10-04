# UnscopedUserSettingsStore.cs

## Role and type

`UnscopedUserSettingsStore` is an internal `ISettingsStore<UserSettings>` sentinel registered in the LocalSystem Agent host where a default user store might otherwise resolve to the service account's `%LOCALAPPDATA%`.

## Invariants and dependencies

It has no filesystem dependency and never reads or writes data. Production IPC must first select a per-user store from the authenticated token SID and the operating-system profile resolver. It is not used for machine settings.

## Failure behavior and tests

Both `Load` and `Save` throw `InvalidOperationException`, so accidental unscoped access cannot silently succeed or fall back to LocalSystem data. `tests/StorageChronicle.Agent.Tests/AuthenticatedUserSettingsRoutingTests.cs` constructs the sentinel and verifies authenticated per-user selection; the production DI registration is covered by Agent host validation.

## Public types and responsibilities

The sentinel is internal and implements only the user-settings store contract; it owns no profile selection or persistence behavior.

## Inputs and outputs

It accepts a settings object only in `Save`, but rejects it without inspecting or persisting it. It returns no settings and performs no file-content access.

## Threading and lifetime

The sentinel is stateless and safe to register as a singleton.

## OS constraints

It is platform-neutral and has no Windows API dependency.

## Change-sensitive contracts

The fail-closed throwing behavior and its production registration are security-sensitive; replacing it with a default store could redirect one user's settings into the service profile.
