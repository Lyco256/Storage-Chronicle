# StorageEngineOptions.cs

## 役割

追記セグメント、flush、SQLite busy timeout、Zstandard、容量保護、履歴ブランチの設定と、記録状態・破損セグメント通知の公開型を定義する。容量確認はストレージディレクトリの空き容量だけを扱い、監視対象ファイルの内容は読まない。

## 公開型と不変条件

- `StorageRecordKind` は source/canonical の2種類だけを表す。
- `StorageEngineOptions` のflush初期値は5秒、閉じたセグメントは既定でZstandard化される。
- `RecordingStatus` は最終内部Sequenceと最終Source Sequenceを保持する。
- `StorageCapacityException` は容量不足時に記録を停止したことを表す。自動削除は行わない。

## 依存関係・失敗動作

ドメインの `SourceSequence` などを参照する。容量プローブが失敗した場合も記録を停止し、SQLiteへ可能な範囲で停止状態を保存する。

## 関連テスト

`StorageEngineTests.CapacityFailureStopsRecordingAndDoesNotDeleteHistory` が容量停止を検証する。

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
