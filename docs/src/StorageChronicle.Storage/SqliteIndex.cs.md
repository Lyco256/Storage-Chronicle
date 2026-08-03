# SqliteIndex.cs

The index exposes bounded payload-page and count queries used by the Agent projection adapter. Queries are parameterized, ordered by recorded/source/segment sequence, and never expose file contents or content hashes.

The bounded append support resolves existing event identities in chunks and writes a finite batch in one `synchronous=FULL` transaction. Segment files remain the source of truth, so a failure after segment append is recoverable by SQLite rebuild.

Canonical state and projection-cache recovery uses the same finite batch boundary and parameterized commands; each batch commits atomically and cancellation is checked between records.

## 役割

追記ログを正本とするSQLite索引・現在状態・projection cacheを保持する。SQLiteは`Microsoft.Data.Sqlite`を直接使用し、WAL、`synchronous=FULL`、foreign keys、有限busy timeoutを設定する。

## スキーマ

明示的なmigration version 1で`event_index`、`path_search`、`current_state`、`process`、`volume`、`mount_session`、`projection_cache`、`storage_metadata`を作成する。path検索はFileId/親/名前/Sequenceで保持し、重複したフルパス文字列を正本として保存しない。

## 不変条件・回復

イベント索引や状態は再生成可能なキャッシュであり、追記ログを更新・削除しない。`RecreateAsync`はSQLite本体とWAL/SHMだけを再作成し、ログの各レコードを再読して索引とcanonical stateを復元する。SQLiteの新規・破損・削除時もengineが再初期化する。

## 関連テスト

実SQLiteのWAL設定・テーブル、削除後の再構築、migration、状態適用、トランザクション境界を`StorageEngineTests`が検証する。

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
