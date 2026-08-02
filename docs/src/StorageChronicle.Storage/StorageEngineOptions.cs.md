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
