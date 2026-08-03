# DiffTreeView.cs

## 役割

path 投影を固定ガター付きの遅延 Tree に構成し、展開済み部分だけを可視行として返す。

## 公開型と不変条件

`DiffTreeNode` は child loader を保持し、`ExpandAsync` 前には descendants を materialize しない。仮想 ancestor、仮想削除、Unknown location root は表示可能だが、projection の事実と混同しないよう `IsVirtual` を保持する。`DiffTreeView` は全ノードを一括 Control 化せず、visible rows だけを返す。

## 依存関係と失敗動作

Projection の path resolver と FileDiffProjection に依存する。空の child loader は空行を返し、キャンセルは expansion 呼び出し元へ返す。

## 関連テスト

Headless テストが root、lazy expansion、virtual unknown/deleted row、gutter icon を検証する。

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
