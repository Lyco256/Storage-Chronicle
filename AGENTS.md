# Storage Chronicle repository instructions

## Source of truth

- `TOP_CODEX.md` defines the top-agent integration responsibility and execution waves.
- `Requirements/00_PRODUCT_REQUIREMENTS.md` through `Requirements/99_TECHNICAL_REFERENCES.md` are the authoritative requirements.
- Do not resolve requirement conflicts by assumption. Stop the affected change and report the exact conflicting sections to the top agent.
- Do not edit files under `Requirements/` unless the user explicitly changes the requirements.

## Branches and delegation

- The initial integration branch is `devenv`; `main` is the stable branch.
- Only the top agent may modify or merge `main` and `devenv`.
- Feature work must use the assigned `feat/*` branch and its dedicated worktree.
- Subagents may edit only the ownership paths named in their assigned requirement file.
- Do not rebase, force-push, rewrite existing history, or merge another branch from a subagent worktree.
- Shared-contract changes belong to the top agent. Request them in `docs/handoffs/<branch-slug>.md`; never create duplicate substitute types.

## Architecture invariants

- Keep source acquisition, canonical event recording, state reconstruction, projection/grouping, persistence, IPC, and UI as separate modules.
- Source facts and their quality must remain distinguishable from UI interpretation or correlation.
- Read/open/query-only I/O is not durable history; retain it only in bounded transient correlation buffers.
- Never read or store file contents or file-content hashes.
- Never add history-deletion behavior.
- Do not generate one synthetic event per descendant for directory moves, renames, or deletions; reconstruct descendants from versioned parent relationships and preserved state.
- Keep platform-neutral contracts and projections usable by a future Ubuntu collector. Isolate Windows-specific APIs under the Windows platform projects.
- The MVP must not require a filesystem minifilter driver, but collector contracts must allow one to be added later.

## Code and documentation

- Treat warnings and nullable warnings as errors.
- Add tests for success, failure, corruption, cancellation, and recovery paths affected by a change.
- Every source file under `src/` must have a matching explanation at the same relative path under `docs/src/`, with `.md` appended.
- Document each file's role, public types, invariants, dependencies, failure behavior, and relevant tests.
- Public APIs require XML documentation.
- Do not commit generated outputs, build artifacts, test results, benchmark results, secrets, or machine-specific files.

## Validation

- Use the scripts under `build/` once the foundation phase creates them.
- Before handoff, run the tests required by the assigned requirement file and the fast repository validation.
- A subagent handoff is incomplete unless its worktree is clean, all changes are committed, tests pass, mirrored documentation exists, and `docs/handoffs/<branch-slug>.md` records changes, commands, results, and known limitations.
- A feature is not complete when behavior is stubbed, hidden behind fake data, untested, undocumented, or dependent on an unapproved shared-contract change.
