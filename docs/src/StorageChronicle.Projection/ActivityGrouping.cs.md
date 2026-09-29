# ActivityGrouping.cs

Activity builders cache gap state and the first descendant-folder anchor after it is resolved. This keeps unknown-process grouping and route compatibility bounded for large Event Stack workloads without changing grouping semantics.

Process property lookup is case-insensitive so safe-property canonicalization cannot hide a recorded parent Process Instance or executable identity.

Canonical Eventを時刻順に走査し、Process Instance、Unknown actor、Volume、Mount Session、表示ルート、無操作timeoutでActivity Groupを生成する。異なる場所の活動は同時にactiveなGroupとして保持し、同じProcess/routeに戻ったイベントはtimeout内なら元のGroupへ戻す。Unknownは同じOrigin、Volume、Mount Session、route、timeoutの範囲だけを統合し、Exact/Correlated actorとは混合せず、別媒体や別取得元を跨がない。

異なる既知Processが同一/祖先/子孫の表示ルートを変更すると先行Activityをそのイベント時刻で閉じ、同一Processの別routeは閉じない。timeout終了境界は最後の変更時刻+設定timeoutとして保持する。Unknown間の別mount/route差は競合Processと推測せず独立させる。read-only観測は境界を作らず、UnverifiedGapは独立Groupにする。表示ルートは製品要件15章のアンカー手順に従い、新規フォルダー配下だけなら新規フォルダー、空フォルダーだけなら親を返す。

ProcessCatalogは記録済みの親Process Instanceだけを辿り、explorer.exeはExplorer操作、Unknownは不明なプロセスとして表示する。主なテストは同時route、interleaved resume、timeout境界、Unknown actor隔離、競合境界、親子Process、10万イベント fixture（`tests/StorageChronicle.Projection.Tests/EventStackProjectionTests.cs`、`tests/StorageChronicle.Projection.Tests/ResilienceAndScaleTests.cs`）。

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
