# NamedPipeAgentServer.cs

## Role and public type

`NamedPipeAgentServer` owns the local versioned JSON IPC endpoint and dispatches projection, settings, health, clipboard, reconciliation, and media-consent requests after authenticating the named-pipe client. It enforces message-size/protocol gates and the endpoint's identity/role rules.

## Invariants and dependencies

Interactive User Settings snapshot/update and Pane-timeout projection use a store freshly resolved from the authenticated pipe-token SID and matching Windows profile. Payload SID/path values are ignored. A missing resolver, non-current session, or unavailable profile is rejected; there is no LocalSystem-profile fallback. Machine settings remain machine-scoped and require administrator identity. The server depends on the Agent settings service, storage/projection services, Windows pipe identity, and the profile-store resolver. No file contents or content hashes are exposed.

## Failure behavior and tests

Malformed messages, authorization failures, profile lookup errors, cancellation, and disconnected clients fail closed without stopping the Agent listener. `tests/StorageChronicle.Agent.Tests/AuthenticatedUserSettingsRoutingTests.cs` verifies two-SID isolation and hostile payload SID/path rejection. Existing Agent IPC tests cover endpoint behavior. Synthetic dispatch tests do not replace live multi-user token/profile service acceptance; consent UI executable-path verification is not an Authenticode signature check.

## Public types and responsibilities

`NamedPipeAgentServer` is the hosted IPC endpoint. Authentication establishes caller identity; handlers enforce operation-specific authorization and delegate settings persistence to the settings service/store.

## Inputs and outputs

Inputs are versioned bounded IPC envelopes. Responses contain protocol data and event metadata only; no file contents or hashes are exchanged.

## Threading and lifetime

The hosted listener owns pipe instances and request cancellation. Per-request profile resolution is not cached, and client disconnect/cancellation is isolated from the service lifetime.

## OS constraints

Named-pipe token and session identity are Windows-specific. The settings contracts and serialized envelopes remain platform-neutral.

## Change-sensitive contracts

Message names/shapes, authenticated-SID provenance, authorization gates, and fail-closed resolver behavior are security- and compatibility-sensitive.
