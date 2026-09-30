# EventStackViewModel.cs

Maintains the bounded Source/Normalized/Grouped page, filters, live-follow state, details, process navigation, and independent expansion state for the operation-group, file-summary, normalized, and source levels.

## 役割

Avalonia/MVVM Event Stackの相互作用状態を所有する。Source、Normalized、Groupedの3モード、同一filter/sort query、展開、ページング、選択、詳細、Live追従、キーボード操作をまとめる。

## 不変条件

- 初期モードはGrouped、初期順序はDescending（新しいものが上）。
- `LoadPageAsync`は投影境界から要求ページだけを受け取り、その件数だけ`EventStackItemViewModel`を生成する。10万件を全件VM化しない。
- Groupedの子は`EventStackPage`の同一グループ内に返され、ViewModelはページ境界で子を分断しない。
- Source Eventは書換えず、Storage/Windows APIは参照しない。
- Live追従は過去スクロールで停止し、Current操作で1ページ目へ戻って再開する。

## 公開状態・操作

品質、Origin、記録時刻、ローカルオフセット、Source/Mount Sequence、親/子Processを詳細に表示できる。`EventStackKey`を介して上下、左右展開、ページ移動、Home/End、Escapeを処理する。保存済みフィルターとMaterial Iconsの意味ID解決もUI境界で扱う。

## 依存関係・失敗動作

CommunityToolkit.Mvvm、Domain/Contracts/UI.Sharedのプラットフォーム中立型に依存する。ページ取得はasyncのみで、キャンセルは投影境界へ渡す。取得失敗は呼び出し元へ返し、UIが同期I/Oを実行しない。

## 関連テスト

`EventStackViewModelTests`が3モード、選択維持、展開、ページ境界、順序、Live、filter、品質、未知Process、10万件ページ生成、キーボードを検証する。

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
