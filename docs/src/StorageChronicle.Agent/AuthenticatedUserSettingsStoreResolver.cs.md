# AuthenticatedUserSettingsStoreResolver.cs

## Role and public types

`IAuthenticatedUserSettingsStoreResolver` selects a `UserSettings` store from a SID already authenticated by the named-pipe token. `IWindowsUserProfileResolver` resolves that SID's `LocalAppData`. `WindowsUserProfileResolver` reads the matching SID's ProfileList and loaded User Shell Folders keys with writable access disabled. `WindowsAuthenticatedUserSettingsStoreResolver` creates the fixed product store through `UserSettingsStore.ForAuthenticatedUser` and resolves the profile afresh on each call.

## Invariants and dependencies

The SID comes only from the authenticated IPC identity; no request-provided path, filename, or SID is accepted. Only `USERPROFILE`, `SystemDrive`, and `SystemRoot` substitutions are resolved. Unresolved variables, UNC/non-local paths, missing registry entries, and unavailable profile directories fail closed. The implementation is Windows-specific and depends on read-only Windows Registry access, `SecurityIdentifier`, and the Settings store factory. It performs no registry or profile mutation and never accesses file contents.

## Failure behavior and tests

Invalid SIDs, inaccessible or unloaded profile data, and invalid paths fail the calling request rather than falling back to the LocalSystem profile. `tests/StorageChronicle.Agent.Tests/AuthenticatedUserSettingsRoutingTests.cs` covers isolated stores for two authenticated SIDs and ignores spoofed payload SID/path fields. Live registry/profile behavior is still a Windows service acceptance item.

## Public types and responsibilities

The resolver interfaces and Windows implementations are documented above; only the Agent chooses the authenticated identity, and this component only maps it to the corresponding profile store.

## Inputs and outputs

Input is an authenticated SID. Output is the fixed-path `ISettingsStore<UserSettings>` for its resolved LocalAppData directory. Arbitrary paths, file contents, and file-content hashes are not accepted or returned.

## Threading and lifetime

Registry handles are scoped to a single synchronous resolution call and disposed before returning. Stores are created per resolution and do not cache profile mappings.

## OS constraints

This resolver is Windows-specific. Profile registry keys are opened read-only; platform-neutral settings contracts remain in the Settings project.

## Change-sensitive contracts

SID authentication provenance, fixed path construction, environment expansion rules, and fail-closed behavior are security-sensitive.
