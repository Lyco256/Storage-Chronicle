# New-ManualAcceptanceBundle.ps1

Generates the manual physical-acceptance bundles required by Requirements 26 and 29. It copies only an existing real MSI, self-contained publish outputs, and the real acceptance scripts; it never invents update/rollback packages. Without `-AllowIncompleteBundle`, missing base/updated/rollback MSI inputs or published outputs fail closed. Generated directories live under ignored `artifacts/manual/` and contain a non-eligible bundle manifest, payload SHA-256 manifest, README, target verification, human confirmation runner, result collector, and explicit cleanup script. The README uses the shared TestLab `.storage-chronicle-testlab-marker.json` contract for the dedicated data root.

The generator performs no installation, service registration, physical-machine mutation, Hyper-V enablement, or deletion. `-Force` applies only to the generated artifact directory and must not be used for user-data paths.

Tests: PowerShell parser validation, strict missing-input failure, and preparation-bundle manifest/hash inspection.
