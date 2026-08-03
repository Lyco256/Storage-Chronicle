# AppendOnlyStorageEngine.cs

The Agent also uses its bounded indexed canonical/source page and count methods for IPC projections; this avoids reading the entire immutable log into memory for every UI page. Segments remain authoritative and SQLite remains rebuildable. A validated log-storage setting can relocate history to a new empty directory by flushing, copying only immutable segments, rebuilding SQLite, and swapping writers under the writer gate; failures leave the original writer active.

`AppendCanonicalBatchAsync` accepts at most 512 events per call. It preserves event order in immutable segments, performs one coupled SQLite transaction after the segment appends, and retains the existing rebuild-from-segments recovery boundary; it does not read file contents or content hashes.

SQLite recovery and relocation rebuild in finite 512-record batches, keeping the log authoritative while avoiding an unbounded in-memory event list or a transaction per recovered record.

## 役割

`IEventStore`と`IStateStore`を実装し、SegmentLogとSqliteIndexを単一writer境界で統合する。source eventはSQLiteなしでもセグメントから読み出せ、canonical eventの状態適用だけがSQLite state cacheを更新する。

## 書込み順序

容量を事前確認した後、JSON化したイベントを追記セグメントへ書き、成功後にSQLite索引へ反映する。SQLiteが失敗してもログが正本であり、`RebuildSqliteAsync`で復旧できる。rename/move/delete、media removal属性、停止時は優先flushする。周期flushの既定値は5秒で、失敗は`StatusChanged`へ通知する。

## 停止・キャンセル・容量

容量不足や実書込み失敗は記録を停止し、最終内部Sequence/Source SequenceをSQLiteへ可能な範囲で保存する。無制限メモリ保持や自動ログ削除は行わない。キャンセルは追記前に尊重し、途中で切断された末尾は次回起動時にSegmentLogが切り捨てる。

## 依存関係・テスト

共有契約、Domain contracts、SegmentLog、SqliteIndexに依存する。`StorageEngineTests`は正常往復、実SQLite、再構築、破損スキップ、容量停止、キャンセル、single-writer、複数reader、履歴移動を実行する。

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
