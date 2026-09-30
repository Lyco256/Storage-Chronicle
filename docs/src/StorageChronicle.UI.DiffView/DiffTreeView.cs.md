# DiffTreeView.cs

## 役割

path 投影を固定ガター付きの遅延 Tree に構成し、展開済み部分だけを可視行として返す。

## 公開型と不変条件

`DiffTreeNode` は child loader を保持し、`ExpandAsync` 前には descendants を materialize しない。仮想 ancestor、仮想削除、Unknown location root は表示可能だが、projection の事実と混同しないよう `IsVirtual` を保持する。`DiffTreeView` は全ノードを一括 Control 化せず、visible rows だけを返す。

## 依存関係と失敗動作

Projection の path resolver と FileDiffProjection に依存する。空の child loader は空行を返し、キャンセルは expansion 呼び出し元へ返す。

## 関連テスト

Headless テストが root、lazy expansion、virtual unknown/deleted row、gutter icon を検証する。

## Role
Projects shared Diff View state into a lazy tree presentation.

## Public types and responsibilities
`DiffTreeView` provides tree roots and on-demand child expansion/collapse while retaining the relationship to projected paths.

## Inputs and outputs
Inputs are projected rows and explicit expansion requests; outputs are tree nodes and visible descendants. File contents and content hashes are not inspected.

## Dependencies
Depends on platform-neutral Diff View contracts and the shared projection model; desktop controls consume its results.

## Invariants
Descendants are materialized on expansion only. Grouping nodes are presentation constructs and do not create canonical events or alter recorded state.

## Threading and lifetime
Tree operations are caller-driven; child loading observes caller cancellation and retains no OS resource.

## Failure behavior
Unavailable descendants remain absent or unknown rather than being queried from the host filesystem; cancellation propagates to the caller.

## Tests
See `tests/StorageChronicle.UI.Headless.Tests` for roots, lazy expansion, virtual rows, and gutter rendering.

## OS constraints
Tree construction is platform-neutral and makes no operating-system filesystem calls.

## Change-sensitive contracts
Root selection, lazy expansion, and separation of grouping rows from source records are compatibility-sensitive.
