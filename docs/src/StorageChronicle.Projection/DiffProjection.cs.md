# DiffProjection.cs

Live、Period、Point-in-Time、Replayを同じファイル投影モデルへ変換する。期間内のFile ID単位履歴を保持し、作成後削除、複数名前変更、移動後編集、Share/Cloudのサブ操作、削除済み仮想項目、場所不明仮想ルートを表現する。

主操作は固定優先順位で選び、期間末が削除なら削除を最優先する。Moveは旧パスを移動元、新パスを移動先として同じ関連操作IDを返す。DiffSemanticStateはUI色そのものではなく意味的状態で、TreeとExplorerは同じFileDiffProjectionを利用する。Replayではイベント単位のPane lifecycleを返す。

ファイル内容、ハッシュ、OS Explorer APIは参照しない。品質は最後の記録済みイベントから保持し、削除済み/場所不明項目は開く操作を無効化する。主なテストはPeriod優先順位、Move/Share/Cloud、Unknown location、Replay。

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
