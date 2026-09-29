# UI Diff View

`StorageChronicle.UI.DiffView` は、共通 File System Projection を Tree/Explorer の二つの Headless renderer に分配する UI 層である。Tree は path ancestor と descendant を expansion 時だけ materialize し、Explorer は bounded page を返す。

表示には8 Explorer mode、primary/sub gutter icon と color token、仮想削除/不明場所 root、Explorer open 可否、path navigation、split panes、Live pause、Replay timeline/cursor/speed、Move reciprocal navigation が含まれる。OS のファイル API は `IExplorerLauncher` の外部 adapter に限定される。

## Role
Documents the platform-neutral Diff View module and its boundary with the desktop shell.

## Public types and responsibilities
The module provides view contracts, a projection-backed ViewModel, lazy Tree rendering, bounded Explorer rendering, lexical navigation, split-pane state, and replay state. It does not duplicate canonical event types.

## Inputs and outputs
Projection records and explicit UI commands enter the module; tree/Explorer rows and view state leave it. File contents and content hashes are never inputs.

## Dependencies
Depends on shared contracts, projection services, and UI.Shared abstractions. Desktop rendering and OS Explorer launching are external adapters.

## Invariants
Source facts and quality remain explicit; grouping is not durable history. Tree expansion and Explorer paging are bounded/on-demand. Live pause affects presentation only.

## Threading and lifetime
Projection queries are asynchronous and cancellation-aware. Renderers are caller-owned and retain no OS resources.

## Failure behavior
Projection errors and cancellation remain visible; navigation does not infer host filesystem existence.

## Tests
See `tests/StorageChronicle.UI.DiffView.Tests` and `tests/StorageChronicle.UI.Headless.Tests`.

## OS constraints
Module behavior is platform-neutral and performs no host filesystem enumeration.

## Change-sensitive contracts
Public contracts, mode identifiers, quality/openability semantics, and the projection/UI boundary must remain synchronized with source mirrors.
