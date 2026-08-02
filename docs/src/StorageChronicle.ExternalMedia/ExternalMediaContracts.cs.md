# ExternalMediaContracts.cs

Defines the platform-neutral media mirror contracts: format/schema/projection versions, volume capability and mirror policy records, quality and recovery outcomes, import ledgers, media-only filters, monitoring exclusion registration, and the deterministic clock abstraction.

Invariants: a mirror is never enabled for system, boot, recovery, or EFI volumes; media quality remains explicit rather than being upgraded by a UI; the import ledger is keyed by immutable manifest/segment SHA-256 values; and no contract creates or changes a USN journal. Tests cover policy rejection, quality classification, filtering, and mount-session clock behavior.
