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
