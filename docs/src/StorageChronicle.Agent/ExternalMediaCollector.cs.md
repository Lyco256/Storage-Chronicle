# ExternalMediaCollector.cs

`WindowsExternalMediaCollector` adapts Windows device arrival/removal notifications into source facts. It creates mount sessions, records media quality and removal gaps, and imports only validated external-media mirror history through the immutable media store and import ledger.

The collector owns no file-content reads. It preserves source quality, media identity, mount sequence, and recovery information as properties for the normalizer and downstream projections. Read-only, system, boot, recovery, and EFI media are rejected by the media-store policy. Mirror import failures are isolated and do not terminate the Agent collector loop.

Public types are `IExternalMediaChangeSource`, `WindowsExternalMediaChangeSource`, and `WindowsExternalMediaCollector`. Dependencies are the Windows volume/notification adapters, machine settings, external-media contracts, and the platform-neutral source-event contract. Tests cover connection, removal, enumeration failure, mirror policy, and import behavior in `StorageChronicle.ExternalMedia.Tests` and Agent integration tests.
