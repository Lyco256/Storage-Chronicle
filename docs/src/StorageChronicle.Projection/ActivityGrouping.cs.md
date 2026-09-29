# ActivityGrouping.cs

Activity builders cache gap state and the first descendant-folder anchor after it is resolved. This keeps unknown-process grouping and route compatibility bounded for large Event Stack workloads without changing grouping semantics.

Process property lookup is case-insensitive so safe-property canonicalization cannot hide a recorded parent Process Instance or executable identity.

Canonical Eventを時刻順に走査し、Process Instance、Unknown actor、Volume、Mount Session、表示ルート、無操作timeoutでActivity Groupを生成する。Unknownは同じOrigin、Volume、Mount Session、ルート、timeoutの範囲だけを統合し、別媒体や別取得元を跨がない。

他Processが同一/祖先/子孫の表示ルートを変更すると先行Activityを直前イベントで閉じ、元Processの再開は新Groupにする。read-only観測は境界を作らず、UnverifiedGapは常に独立Groupにする。表示ルートは製品要件15章のアンカー手順に従い、新規フォルダー配下だけなら新規フォルダー、空フォルダーだけなら親を返す。

ProcessCatalogは記録済みの親Process Instanceだけを辿り、explorer.exeはExplorer操作、Unknownは不明なプロセスとして表示する。主なテストはtimeout、Unknown、競合、親子Process、10万イベント fixture。

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
