# DiffNavigation.cs

## 役割

分割枠、Tree/Explorer の path navigation、Replay cursor/speed/play state を UI 内で保持する。

## 公開型と不変条件

`DiffSplitPaneState` は ratio を0.1～0.9に制限し、`DiffReplayState` は speed を0.25～16倍に制限する。Pane state は選択 path だけを持ち、projection や durable history を変更しない。

## 依存関係と失敗動作

外部依存はない。範囲外 ratio/speed は `ArgumentOutOfRangeException` で拒否する。

## 関連テスト

Headless テストが back/forward、split ratio、Move endpoint、Replay timeline を検証する。

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
