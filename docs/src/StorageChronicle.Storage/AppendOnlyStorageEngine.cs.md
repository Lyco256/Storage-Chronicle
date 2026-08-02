# AppendOnlyStorageEngine.cs

The Agent also uses its bounded indexed canonical/source page and count methods for IPC projections; this avoids reading the entire immutable log into memory for every UI page. Segments remain authoritative and SQLite remains rebuildable. A validated log-storage setting can relocate history to a new empty directory by flushing, copying only immutable segments, rebuilding SQLite, and swapping writers under the writer gate; failures leave the original writer active.

## 役割

`IEventStore`と`IStateStore`を実装し、SegmentLogとSqliteIndexを単一writer境界で統合する。source eventはSQLiteなしでもセグメントから読み出せ、canonical eventの状態適用だけがSQLite state cacheを更新する。

## 書込み順序

容量を事前確認した後、JSON化したイベントを追記セグメントへ書き、成功後にSQLite索引へ反映する。SQLiteが失敗してもログが正本であり、`RebuildSqliteAsync`で復旧できる。rename/move/delete、media removal属性、停止時は優先flushする。周期flushの既定値は5秒で、失敗は`StatusChanged`へ通知する。

## 停止・キャンセル・容量

容量不足や実書込み失敗は記録を停止し、最終内部Sequence/Source SequenceをSQLiteへ可能な範囲で保存する。無制限メモリ保持や自動ログ削除は行わない。キャンセルは追記前に尊重し、途中で切断された末尾は次回起動時にSegmentLogが切り捨てる。

## 依存関係・テスト

共有契約、Domain contracts、SegmentLog、SqliteIndexに依存する。`StorageEngineTests`は正常往復、実SQLite、再構築、破損スキップ、容量停止、キャンセル、single-writer、複数reader、履歴移動を実行する。
