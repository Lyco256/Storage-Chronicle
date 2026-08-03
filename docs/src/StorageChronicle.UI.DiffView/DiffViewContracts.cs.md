# DiffViewContracts.cs

## 役割

共通 Projection と UI の間の local adapter、gutter visual token、Explorer row、Explorer launcher の platform-neutral 契約を定義する。

## 公開型と不変条件

`IDiffProjectionSource` は rich `DiffProjection` を Tree と Explorer へ共有する。`ContractDiffProjectionSource` は既存 `IProjectionService` の `DiffEntry` を表示用に包むだけで、ファイル内容を取得しない。`DiffVisuals` は semantic state を stable color/icon token に変換し、`IExplorerLauncher` は現在存在し open 可能な行にだけ呼び出される。

## 依存関係と失敗動作

Domain、Contracts、Projection の公開型だけに依存する。仮想・削除・不明場所は `ExplorerOpenResult.Rejected` で拒否し、headless default launcher は OS I/O を行わない。

## 関連テスト

DiffViewModel の Headless テストが adapter 経由の rich rows、visuals、Explorer launcher の可否を検証する。

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
