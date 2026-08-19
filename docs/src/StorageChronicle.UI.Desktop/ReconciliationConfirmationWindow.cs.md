# ReconciliationConfirmationWindow.cs

`ReconciliationConfirmationWindow` is the only desktop confirmation surface for a continuity gap. It renders the fixed Japanese warning, the volume/file-system/reason/uncertain interval facts supplied by the Agent, and the two explicit choices `実行する` and `実行しない`. The selected boolean is returned to `AgentHealthPanel`, which sends the existing typed IPC decision; the dialog never scans a volume itself.

## Role

Modal Avalonia UI boundary for the user decision required before confirmed reconciliation.

## Public types and responsibilities

`ReconciliationConfirmationWindow` owns presentation and modal result handling. It does not own Agent state, storage, collection, or filesystem I/O.

## Inputs and outputs

Input is one `PendingReconciliationRequest`, including the volume, filesystem, reason, discovery time, and optional last-continuous boundary. Output is `true` for `実行する`, `false` for `実行しない`, or `null` if the window is dismissed.

## Dependencies

Avalonia controls/automation and the shared runtime/UI contracts.

## Invariants

The fixed warning remains visible; no user choice bypasses the Agent IPC decision path; no filesystem content or hash is read.

## Failure behavior

Closing the dialog without a choice returns `null`, so the caller performs no decision.

## Threading and lifetime

The window is created and shown on the Avalonia UI thread. Its modal task completes exactly once when a button closes the window or when the window is dismissed.

## Tests

`tests/StorageChronicle.UI.Headless.Tests/HeadlessSmokeTests.cs` verifies the fixed warning and both button labels/results.

## OS constraints

The view is platform-neutral Avalonia UI; Windows-only behavior remains in the Agent/platform projects.

## Change-sensitive contracts

The fixed Japanese warning and explicit `実行する`/`実行しない` labels are part of the reconciliation acceptance contract. The dialog must not expose a manual scan command or bypass the typed Agent decision IPC.
