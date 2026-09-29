# ProjectionService.cs

IProjectionServiceのOS非依存実装。immutableなProjectionDocumentを受け、Event StackとDiffを必要時に再生成する。追加APIではフィルター付きEvent Stack、子孫を含むTree page、詳細Diff projectionを提供する。

CancellationTokenは投影処理開始前に検査する。永続履歴の追記・更新・削除は行わず、Avalonia、Storage、Windows Collectorへ依存しない。標準契約のEventStackRow/DiffEntryに加えて、UIが必要とする意味的詳細DTOを返す。

主なテストはキャンセル、3モード、ページング、Diff共有モデル、再生成時の入力不変性。

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
