# サブエージェント要件：Storage Engine

## ブランチ

`feat/storage-engine`

## 所有パス

- `src/StorageChronicle.Storage/**`
- `tests/StorageChronicle.Storage.Tests/**`
- 対応する`docs/src/StorageChronicle.Storage/**`
- `docs/handoffs/storage-engine.md`

## 目的

Source EventとCanonical Eventの追記正本、SQLite索引、状態スナップショット、圧縮、破損復旧を実装する。

## 実装要件

### 追記セグメント

- 不変の分割セグメント。
- 明示的なファイル形式ヘッダー、format version、segment ID。
- レコード長、型、schema version、sequence、payload、CRC32C。
- セグメント確定時にSHA-256を計算し、マニフェスト参照と媒体分岐識別へ使用する。ファイル内容のハッシュではない。
- 未完了末尾を検出して安全に切り捨てる。
- 確定済みレコードを書換えない。
- セグメントローテーション。
- 閉じたセグメントだけZstandard圧縮。
- 圧縮前後の内容検証。
- 一個の破損セグメントで他を読めなくしない。
- flush間隔は設定可能、初期値5秒。
- 削除、名前変更、媒体取り外し、停止時は優先flush。
- flush失敗を上位へ通知し、黙って継続しない。

### SQLite

- Microsoft.Data.Sqlite直接使用。
- `journal_mode=WAL`。
- `synchronous=FULL`、`foreign_keys=ON`、有限の`busy_timeout`。
- 現在状態、イベント索引、パス検索、Process、Volume、Mount Session、Projection Cache。
- SQLiteは正本にしない。
- DB削除・破損時に追記ログから再構築。
- マイグレーションを明示的にバージョン管理。
- トランザクション境界をテスト。
- フルパス文字列の重複を避ける。

### 容量不足

- 事前残量確認だけに依存せず実書込み失敗を処理する。
- 記録停止状態を返す。
- 自動削除しない。
- メモリへ無制限保持しない。
- 最終確定Source Sequenceを保存する。

## テスト

- 正常往復。
- 途中終了。
- 末尾切断。
- チェックサム不一致。
- 中間セグメント破損。
- 圧縮破損。
- SQLite削除・再構築。
- migration。
- 容量不足Fault Injection。
- 同時readerとsingle writer。
- キャンセル。
- 100万小レコード性能fixture。

## 受け入れ条件

- ログ削除APIがない。
- ファイル内容を読むコードがない。
- 既存確定ログを更新しない。
- SQLiteなしでもSource Eventを読み出せる。
- 一部破損時に健全セグメントを列挙できる。
