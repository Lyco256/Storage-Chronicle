# DiffViewContracts.cs

## 役割

共通 Projection と UI の間の local adapter、独立ページングActivity Frame付きbundle、ページ方向指定、gutter visual token、Explorer row、Explorer launcher の platform-neutral 契約を定義する。

## 公開型と不変条件

`IDiffProjectionSource` は rich `DiffProjection` とWire契約上のActivity Frame要約をTree、Explorer、Frameペインへ共有する。Frameは独立ページ番号、サイズ、昇順/新しい順を持ち、サーバーがページ選択前に順序を適用する。`ContractDiffProjectionSource` は既存 `IProjectionService` の `DiffEntry` を表示用に包むだけで、Frameの仮データを生成せず、ファイル内容も取得しない。`DiffVisuals` は semantic state を stable color/icon token に変換し、`IExplorerLauncher` は現在存在し open 可能な行にだけ呼び出される。

## 依存関係と失敗動作

Domain、Contracts、Projection の公開型だけに依存する。仮想・削除・不明場所は `ExplorerOpenResult.Rejected` で拒否し、headless default launcher は OS I/O を行わない。

## 関連テスト

DiffViewModel の Headless テストが adapter 経由の rich rows、visuals、Explorer launcher の可否を検証する。

## Role
Defines platform-neutral value contracts shared by Diff View projection and rendering.

## Public types and responsibilities
The records and enums represent display modes, projected rows, quality markers, and Explorer eligibility without duplicating canonical event contracts.

## Inputs and outputs
Contracts carry already-projected metadata between UI components. File contents and file-content hashes are outside the contract.

## Dependencies
Depends on shared Storage Chronicle contracts and value types; it has no desktop or OS dependency.

## Invariants
Source quality and openability remain explicit; display interpretation cannot upgrade unknown facts or create durable history.

## Threading and lifetime
Contracts are immutable values with no asynchronous or resource lifetime.

## Failure behavior
Invalid values are rejected by owning constructors/operations; consumers preserve unknown quality rather than silently coercing it.

## Tests
See `tests/StorageChronicle.UI.DiffView.Tests` and `tests/StorageChronicle.UI.Headless.Tests`.

## OS constraints
Contracts are platform-neutral and contain no OS handles or filesystem access.

## Change-sensitive contracts
Public names, quality values, mode identifiers, and eligibility semantics are compatibility-sensitive.
