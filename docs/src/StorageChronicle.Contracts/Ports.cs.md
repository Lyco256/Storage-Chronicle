# Ports.cs

Defines append-only event, state, normalization, collector, projection, volume, capability, and local message-codec interfaces. Implementations depend on these ports rather than duplicating contracts. Contract tests cover serialization, idempotence, bounded reads, and protocol-version rejection.
