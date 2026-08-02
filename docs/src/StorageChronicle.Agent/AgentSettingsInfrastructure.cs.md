# AgentSettingsInfrastructure

Implements the Agent-side settings authorization context, append-only property-name-only settings history, and storage flush lifecycle. Machine updates require the impersonated named-pipe client to be an administrator; settings values are validated and atomically persisted by `StorageChronicle.Settings`.
