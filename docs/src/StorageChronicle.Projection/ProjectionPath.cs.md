# ProjectionPath.cs

記録済みpath、parentPath、Metadataの親ID、既知の親パスからOS非依存にパスを再構成する。イベント順序はRecordedUtc、SourceSequence、MountSequence、EventIdで決定し、ファイルシステムへ問い合わせない。

Activityの初期アンカーは変更項目の親フォルダーであり、同じ/祖先/子孫関係と新規フォルダーの配下判定をこのファイルの境界判定で行う。兄弟フォルダーを共通祖先へ昇格させない。パス不明はProjection側で仮想ルートへ渡す。

主なテストは新規フォルダー表示ルート、競合プロセス境界、Unknown grouping、Diffの場所不明仮想ルート。

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
