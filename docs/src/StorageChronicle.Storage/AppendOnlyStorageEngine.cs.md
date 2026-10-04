# AppendOnlyStorageEngine.cs

The Agent also uses its bounded indexed canonical/source page and count methods for IPC projections; this avoids reading the entire immutable log into memory for every UI page. Segments remain authoritative and SQLite remains rebuildable. A validated log-storage setting can relocate history only to a path that does not yet exist: it flushes, copies only immutable segments, rebuilds SQLite, and swaps writers under the writer gate. It refuses even an existing empty directory so relocation never removes or adopts a user-created destination; failures leave the original writer active. Before creating or recovering history, the engine requires a valid product ownership marker or an empty directory with no reparse-point ancestors; unmarked directories containing any entries are left untouched and rejected before SQLite recovery can delete files. A marked store is also rejected if any child is a reparse point or an unrecognized entry.

`AppendCanonicalBatchAsync` accepts at most 512 events per call. It preserves event order in immutable segments, performs one coupled SQLite transaction after the segment appends, and retains the existing rebuild-from-segments recovery boundary; it does not read file contents or content hashes. The ownership-entry validator is shared with `ReadOnlyStorageHistoryReader` so writer and reader paths enforce the same recognized child set.

At startup, a missing SQLite index with authoritative segments or a structurally corrupt existing SQLite database triggers a rebuild from the segments. SQLite `quick_check(1)` runs before schema pragmas or migrations; only explicit corruption findings trigger derived-index replacement, while unsupported schema and unrelated I/O failures remain errors. Startup recovery rebuilds in finite 512-record batches, restoring event indexes, canonical state, process-lifecycle rows, and sequence metadata without an unbounded in-memory event list. Explicit SQLite recovery and relocation use the same batched replay path.

Process exit observations are stored as a separate `ProcessLifecycleEvent` segment record, not as a Source or Canonical file event. A bounded background writer persists them off the ETW callback and forces the append segment to disk before publishing its rebuildable SQLite lifecycle-index row. Queue saturation/persistence failure is visible through the collector or Agent health path; graceful disposal drains accepted facts. Startup scans authoritative segments in bounded batches to repair missing lifecycle-index rows; when the whole index is absent/corrupt, the same startup rebuild restores all three record kinds plus the lifecycle rows. Queries are restricted to requested process-instance IDs and UTC intervals so Replay can restore observed exit boundaries after Agent restart without loading all lifecycle history into memory.

## 役割

`IEventStore`と`IStateStore`を実装し、SegmentLogとSqliteIndexを単一writer境界で統合する。source eventはSQLiteなしでもセグメントから読み出せ、canonical eventの状態適用だけがSQLite state cacheを更新する。履歴移設先が既存なら空ディレクトリであっても触らず拒否する。起動時は所有markerと再解析ポイントを確認し、未知ファイルを含む未所有の履歴ディレクトリを復旧・削除しない。

## 起動時回復

既存のSQLite DBはmigrationやWAL設定の前に`quick_check(1)`で検査する。欠落DBとSQLiteが明示した構造破損は、追記ログを正本として索引、canonical state、process-lifecycle索引、Sequence情報を512件ずつ再生成する。破損時の再作成対象は製品領域のSQLite本体とWAL/SHM/rollback-journal sidecarに限定する。未対応の新schema、権限、busy、その他のI/O失敗はDBを削除せず起動失敗として返す。

## 書込み順序

容量を事前確認した後、JSON化したイベントを追記セグメントへ書き、成功後にSQLite索引へ反映する。SQLiteが失敗してもログが正本であり、`RebuildSqliteAsync`で復旧できる。rename/move/delete、media removal属性、停止時は優先flushする。周期flushの既定値は5秒で、失敗は`StatusChanged`へ通知する。

## 停止・キャンセル・容量

容量不足や実書込み失敗は記録を停止し、最終内部Sequence/Source SequenceをSQLiteへ可能な範囲で保存する。無制限メモリ保持や自動ログ削除は行わない。キャンセルは追記前に尊重し、次回起動時にはmanifestのない`.open`セグメントを再検査する。完全なCRC検証済みprefixを保持した上で、未完成レコードの末尾バイトだけを切り詰めて追記を再開する。CRC破損・途中破損・確定済みセグメントは変更せず追記から除外する。

## 依存関係・テスト

共有契約、Domain contracts、SegmentLog、SqliteIndexに依存する。`StorageEngineTests`は正常往復、実SQLite、欠落・構造破損後の起動時再構築、未対応schemaの保護、破損スキップ、容量停止、キャンセル、single-writer、複数reader、履歴移動を実行する。

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
