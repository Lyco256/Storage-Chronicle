# SqliteIndex.cs

The index exposes bounded payload-page and count queries used by the Agent projection adapter. Queries are parameterized, ordered by recorded/source/segment sequence, and never expose file contents or content hashes.

## 役割

追記ログを正本とするSQLite索引・現在状態・projection cacheを保持する。SQLiteは`Microsoft.Data.Sqlite`を直接使用し、WAL、`synchronous=FULL`、foreign keys、有限busy timeoutを設定する。

## スキーマ

明示的なmigration version 1で`event_index`、`path_search`、`current_state`、`process`、`volume`、`mount_session`、`projection_cache`、`storage_metadata`を作成する。path検索はFileId/親/名前/Sequenceで保持し、重複したフルパス文字列を正本として保存しない。

## 不変条件・回復

イベント索引や状態は再生成可能なキャッシュであり、追記ログを更新・削除しない。`RecreateAsync`はSQLite本体とWAL/SHMだけを再作成し、ログの各レコードを再読して索引とcanonical stateを復元する。SQLiteの新規・破損・削除時もengineが再初期化する。

## 関連テスト

実SQLiteのWAL設定・テーブル、削除後の再構築、migration、状態適用、トランザクション境界を`StorageEngineTests`が検証する。
