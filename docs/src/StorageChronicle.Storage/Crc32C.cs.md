# Crc32C.cs

## 役割

セグメントレコードのフレームヘッダーとpayloadに対するCRC32Cを計算する内部実装である。

## 不変条件

CRCはレコードフレームに格納され、読み出し時に必ず再計算して比較する。SHA-256やファイル内容ハッシュの代替には使わない。

## 失敗動作とテスト

不一致はセグメントを破損として扱い、他セグメントの読み出しを妨げない。中間セグメント破損の統合テストは`CorruptClosedSegmentIsSkippedWithoutBlockingHealthySegments`で検証する。

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
