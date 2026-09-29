# Physical data-protection acceptance handoff

## Scope

This handoff records the superseding physical-test safety work on `feat/testlab-virtualbox-migration`. The user clarified that writes are not categorically forbidden: the primary prohibition is irreversible or recovery-difficult changes to existing user, system, or external-media data. Product-owned history writes and mutations confined to newly created, run-owned fixtures are allowed under `Requirements/37_PHYSICAL_READ_ONLY_ACCEPTANCE.md`. Storage Chronicle's own settings may be updated through its normal UI using the existing atomic-replacement and backup flow. VirtualBox/VM execution requirements are historical and are not authorization to run those workflows.

## Changes

- Rewrote `Requirements/37_PHYSICAL_READ_ONLY_ACCEPTANCE.md` to prohibit destructive changes to existing data while allowing approved product-owned writes and isolated fixture operations.
- Bound external-media reads and writes to a Windows volume-identity-pinned directory session; added authoritative role classification and fail-closed behavior for unknown or ambiguous volumes.
- Hardened product history ownership, append-only recovery, settings-path separation, installer acceptance gates, and test-fixture ownership. New source files have matching `docs/src` explanations.
- Disabled automatic installer/VHDX cleanup paths where ownership cannot be independently proven; recorded remaining safety gaps in `docs/release/physical-readonly-audit.md` and machine-readable audit data.
- Updated this handoff from obsolete VM instructions to the current physical test policy.

## Validation commands and results

- `./build/Test-All.ps1`: exit 0 on 2026-09-29; solution build had 0 warnings/0 errors, all fast tests passed, and quality/coverage and UI checks passed.
- Agent tests: 36 passed, 3 physical acceptance tests skipped because no acceptance root was configured.
- ExternalMedia tests: 20/20; Windows filesystem tests: 21/21; Storage: 12/12; Settings: 17/17; Diff View: 6/6; Event Stack: 9/9; UI settings: 13/13.
- Correlation and real-I/O oracle validator suites each passed 4/4, including negative evidence cases that intentionally emit `FAILED` payloads to verify rejection.
- `git diff --check`: no whitespace errors. DocMirror ran as part of the full suite.
- No physical product, service, installer, privileged runner, VHDX workflow, or real external-media mirror was started. These results are not a physical-machine safety certification.

## Known limitations and required user handoff

Physical execution remains **DO NOT RUN** for ordinary monitored volumes, privileged acceptance, installer operations, and cleanup. Host preflight and independent process-attributed write monitoring are `NOT_EXECUTED`. Static review still has material gaps: marker authenticity is forgeable, ancestor `DELETE_CHILD`/directory race resistance is unproven, live partition-role querying is untested, and complete installer/runner source-to-sink review is incomplete. Passing unit tests and skipped acceptance cases are not physical acceptance.

User action is required before physical acceptance: identify and approve a dedicated local NTFS fixture/evidence root on a dedicated test host, review any UAC/installer action, and provide the required Windows 10 22H2 machine if available. Never provide credentials. Code-level safety gates and an independent runtime monitor must pass before starting product or privileged processes.

## Merge gate

The non-privileged code changes may be integrated into `devenv` after review and revalidation. `main` is not ready: outstanding physical gates, product feature gaps, performance measurements, and requirements-coverage items in `docs/release/main-readiness.md` must pass first. Do not run irreversible operations on existing data or merge to `main` while any requirement remains `NOT_EXECUTED` or unmet.
