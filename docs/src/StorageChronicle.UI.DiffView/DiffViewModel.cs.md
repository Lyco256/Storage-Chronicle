# DiffViewModel.cs

## 役割

共通 Projection の rich diff projection を一度だけ取得し、Tree と Explorer に同一行・同一選択・同一ガター情報を提供する Headless UI 状態である。stable `IProjectionService` しか持たない IPC/テストホスト向けにはローカル adapter を使用する。

## 公開型と不変条件

- `DiffViewModel` は Live、Period、PointInTime、Replay を保持し、Live pause 中は UI refresh だけを止め Agent の記録を止めない。
- Explorer の8表示、分割枠、履歴ナビゲーション、Move 相互移動、Replay cursor、Explorer 可否を一元管理する。
- `Rows` は既存 shared contract の `DiffEntry`、`Items`/`ExplorerRows` は rich projection とその表示情報であり、UI が OS filesystem を直接読むことはない。

## 依存関係と失敗動作

`IProjectionService` または `IDiffProjectionSource` と、注入可能な `IExplorerLauncher` に依存する。仮想削除行・不明場所・disabled row は Explorer launcher を呼ばず、理由を表示可能な状態として返す。キャンセルは projection/launcher へ伝播する。

## 関連テスト

`tests/StorageChronicle.UI.DiffView.Tests/DiffViewModelTests.cs` が8表示、Tree、Explorer、ナビゲーション、split、Move、Live pause、Replayを検証する。

## Role

This mirror documents the source boundary for this file and explains how it participates in Storage Chronicle.

## Public types and responsibilities

Public types preserve source facts and the explicitly owned responsibility; UI interpretation and correlation remain outside this boundary.

## Inputs and outputs

Inputs and outputs are the declared contracts of the source file. File contents and file-content hashes are never an input or output.

## Dependencies

Dependencies are limited to the referenced project contracts and platform services shown by the source file.

## Invariants

The source keeps canonical facts distinguishable from reconstructed state and does not synthesize descendant events.

## Threading and lifetime

Callers own cancellation and lifetime; asynchronous work must not outlive the owning pipeline or UI scope.

## Failure behavior

Failure, corruption, cancellation, and recovery remain observable and are not converted into a false successful observation.

## Tests

Validated by tests/StorageChronicle.Integration.Tests and the affected integration tests.

## OS constraints

Platform-neutral behavior remains portable; Windows-only APIs are isolated in the Windows platform projects.

## Change-sensitive contracts

Public names, serialized fields, persistence boundaries, and the mirrored path are compatibility-sensitive contracts.
