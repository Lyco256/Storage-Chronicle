# Test-BranchIntegration.ps1

## Role

Produces the measured `StorageChronicle.BranchIntegrationEvidence.v1` artifact used by final acceptance. It captures the current branch and HEAD, remote `devenv`/`main` availability, worktree cleanliness, the expected accepted SHA, and the ancestry relation required by the release procedure.

## Inputs and outputs

`-ExpectedAcceptedSha` identifies the commit that passed the preceding acceptance gates. `-OutputPath` selects the JSON artifact; otherwise the script writes beneath `artifacts/acceptance/branch-integration/`. The script exits `0` only when the current checkout is clean `main`, both remote branches exist, HEAD equals the expected accepted SHA and `origin/main`, and `origin/main` contains `origin/devenv`. Missing integration state produces a real ineligible artifact and exit code `2`.

## Invariants and failure behavior

The script is read-only with respect to Git history and remote state. It never merges, pushes, rebases, rewrites history, or edits `main`/`devenv`. A missing remote ref, dirty worktree, wrong branch, SHA mismatch, or failed ancestry check remains in `FailureReasons` and cannot satisfy final acceptance.

## Dependencies and lifecycle

It depends on the repository's local Git checkout and the configured `origin` remote. It creates only the requested JSON evidence directory; it does not change Git or repository content. The evidence records the observation time and the exact refs used by the check.

## OS constraints

The script is platform-neutral where PowerShell and Git are available. It must be run from the repository checkout whose state is being integrated; a result from another checkout is not evidence for `main`.

## Change-sensitive contracts

The schema name, required branch/ref fields, `ExpectedAcceptedSha` comparison, clean-worktree check, `origin/main` equality, and `origin/devenv` ancestry check are release contracts. Any field missing or false is ineligible.

## Tests

PowerShell parsing is covered by repository script validation; `build/quality/Test-FinalAcceptance.ps1` validates every required field and rechecks the release invariants from the generated artifact.
