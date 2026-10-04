# `feat/settings` handoff

## Change in this handoff

Added a pre-apply impact review to `SettingsDialogViewModel`. A machine-settings change now produces an old/new preview of exact monitoring and exclusion paths, log destination, media identifier and mirror destination, noise-filter profile, and flush interval. The preview describes affected Agent behavior and limits writes to Agent-authorized product-owned settings/history locations; a mirror append is only allowed after the Agent verifies actual volume identity, allowed role, and product-owned destination, otherwise it must reject. The initial Apply action makes no gateway call for such a change; only the separate confirmation command applies the captured snapshot. Cancel or any edit to either draft invalidates the preview. User-Settings-only changes retain the ordinary Apply path. Gateway failures/exceptions remain errors and do not produce success status.

Added headless tests for preview-before-gateway, exact path/media/behavior display, snapshot invalidation, confirm, cancel, User-Settings-only apply, and failure after confirmation. Updated the mirrored source explanation.

## Validation

Executed serially from `C:\Users\lyco2\.codex\worktrees` so SDK selection does not encounter this repository's pinned `10.0.302` (the host has `10.0.401`):

- `dotnet test "C:\Users\lyco2\.codex\worktrees\settings-impact-review\Storage Chronicle\tests\StorageChronicle.UI.Settings.Tests\StorageChronicle.UI.Settings.Tests.csproj" --no-restore` — PASS, 9/9 (final run after preview-boundary and exception tests).
- `dotnet test "C:\Users\lyco2\.codex\worktrees\settings-impact-review\Storage Chronicle\tests\StorageChronicle.Settings.Tests\StorageChronicle.Settings.Tests.csproj"` — PASS, 13/13.
- `powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\lyco2\.codex\worktrees\settings-impact-review\Storage Chronicle\build\Test-Fast.ps1"` — PASS, exit 0. The solution runner reported the Domain test project, 2/2; it did not report broader project results in this checkout.
- `git diff --check` — PASS (only Git's LF-to-CRLF advisory for edited files).

The first fast-script attempt was blocked by the host PowerShell execution policy; rerunning with process-scoped `-ExecutionPolicy Bypass` succeeded. The SDK-specific invocation used the installed `10.0.401` because the repository pins unavailable SDK `10.0.302`; no repository SDK configuration was changed.

## Limitations

- This repository snapshot has no `Requirements/37_PHYSICAL_READ_ONLY_ACCEPTANCE.md`; its exact product-settings policy section was read from the requirement-updated acceptance worktree. Requirement 09 and `AGENTS.md` were read from this feature worktree.
- The current UI settings project is headless and exposes `ImpactPreview`, `ConfirmImpactAndApplyCommand`, and `CancelImpactPreviewCommand`; it has no concrete dialog view in this ownership path to visually render those properties. A shell binding must present `ImpactPreview` and bind explicit user controls to Confirm/Cancel before this is visually integrated.
- No requirement, shared contract, Agent gateway, physical machine, privileged operation, or external-media write was changed or run.

## Authenticated per-user settings store (2026-10-04)

### Change

Added `WindowsSettingsPathProvider.ForAuthenticatedUser` and `UserSettingsStore.ForAuthenticatedUser`. These derive the fixed `Storage Chronicle/user-settings.json` location from a syntactically valid Windows SID and a fully qualified local LocalAppData root. The SID is not used as a path component. Relative paths, UNC roots, and `.`/`..` path segments are rejected before any filesystem access. The XML/API documentation makes the trust boundary explicit: the SID must come from the authenticated Windows client token and the matching profile root must be resolved by the privileged host; neither may come from an IPC payload. Mirrored source documentation was updated.

Added tests for two different SID/profile roots and independent settings, malformed/out-of-range SIDs, relative/UNC/traversal roots, and recovery from one user's corrupt primary without changing another user's settings. Existing corruption/recovery tests continue to cover defaults and backup fallback.

### Integration request for the top agent (Agent IPC wiring is outside this ownership)

At the authenticated named-pipe connection boundary, take `NamedPipeClientIdentity.Sid` only after pipe-client token authentication. Resolve that SID's Windows profile and its LocalAppData root using trusted OS/profile information; do not use the LocalSystem service's `Environment.SpecialFolder.LocalApplicationData`, and do not accept a SID or path from request JSON/IPC payload. Create/select the per-user store with `UserSettingsStore.ForAuthenticatedUser(identity.Sid, resolvedLocalAppDataRoot)` for that authenticated connection. Route both settings load/apply and any projection behavior that consumes user settings (including Pane timeout) through that same authenticated user's store. Keep Machine Settings on the existing machine-wide store. Add Agent IPC tests with two authenticated SIDs proving isolation and tests that payload SID/path spoofing cannot redirect access. Resolve the profile root from the SID rather than trusting caller-supplied path text; handle missing/unavailable profiles by failing closed without falling back to the LocalSystem profile or a shared User Settings file. No shared contract change is requested.

### Validation for this continuation

- `dotnet test "C:\Users\lyco2\.codex\worktrees\settings-impact-review\Storage Chronicle\tests\StorageChronicle.Settings.Tests\StorageChronicle.Settings.Tests.csproj" --no-restore` from `C:\Users\lyco2\.codex\worktrees` — PASS, 23/23, 0 skipped.
- `powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\lyco2\.codex\worktrees\settings-impact-review\Storage Chronicle\build\Test-Fast.ps1"` from `C:\Users\lyco2\.codex\worktrees` — PASS, exit 0. This checkout's fast script reported Domain 2/2; the dedicated Settings test command above separately compiled the Settings assembly and passed all 23 tests.
- `git diff --check` — PASS; Git emitted only LF-to-CRLF working-copy advisories.
- Manual source/document mirror check — PASS for both changed Settings source files and their same-relative-path `.md` explanations.

### Limitations

- This is the Settings-owned path/store API only. Agent registration, authenticated SID propagation, Windows profile resolution, projection scoping, and corresponding IPC spoof/isolation tests remain required integration work for the top agent.
- The factory validates SID and path syntax but cannot prove that a syntactically valid SID was authenticated or that a profile root belongs to that SID. Those guarantees are intentionally enforced at the Agent boundary, not by accepting untrusted path or identity values here.
- No physical-machine, privileged, or external-media operation was performed.
