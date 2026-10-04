# サブエージェント要件：External Media

## ブランチ

`feat/external-media`

## 開始条件

Wave 1のState、Storage、Windows NTFSが統合済みであること。

## 所有パス

- `src/StorageChronicle.ExternalMedia/**`
- `tests/StorageChronicle.ExternalMedia.Tests/**`
- 対応する`docs/src/StorageChronicle.ExternalMedia/**`
- `docs/handoffs/external-media.md`

## 目的

外付け媒体の識別、Mount Session、PC側履歴、任意媒体ミラー、別PC引継ぎ、分岐、突然取り外しを実装する。

## 実装要件

- 標準はPC側保存。
- 媒体ごとのミラー設定。
- 媒体ミラーはユーザー有効化と37に定めるPCごと・媒体ごとの明示初回承認後、所有を確認した `.StorageChronicle` 専用領域への履歴追記だけを許す。初回承認では既存履歴の読取り/importと以後の追記を説明し、NTFS ACLまたはFAT/exFATの保護制約を正確に提示する。NTFSでは承認操作を行う現在ユーザーの正確なSIDに限り、専用領域内の履歴作成・追記に必要な書込みを許可してよい。広いユーザーグループへの書込み、既存履歴の削除・変更・名前変更、ACL/所有者変更は許可しない。旧形式や既存 `MediaMirrors` 設定だけを新要件の承認とは扱わない。媒体identity、専用領域、PC identityの変化時は再承認する。同名の未知の既存領域には触れず停止する。媒体の既存ユーザーデータを編集・削除・移動・上書きしない。実機受入れは37の静的監査と隔離smokeを先に通す。
- ミラーは媒体関連履歴だけ。
- `.StorageChronicle`専用ディレクトリ。
- 監視除外登録。
- 追記不変セグメント。
- PC別writer領域。
- レコードCRC32Cと確定セグメントSHA-256。
- tempからatomic rename。
- A/B manifest。
- Format、Schema、Projection version分離。
- Mount Session。
- 前Session参照。
- 別PC確定ログ取込み。
- 同一ログの重複取込み防止。
- 分岐マニフェスト。各確定マニフェストは自身のSHA-256、直前親マニフェストSHA-256、論理媒体ID、Writer PC ID、Mount Session IDを持つ。同じ親SHA-256を参照する異なる有効子マニフェストを取り込んだ時点で履歴分岐として確定する。
- 共通過去を複製しない。
- 媒体複製検出。物理複製そのものを推測せず、上記の同一親からの履歴分岐を複製後の分岐として扱う。
- 媒体ログ削除検出と新ブランチ。
- 強制取り外し時は未確定一個だけ修復・破棄。
- 確定済みセグメント不変。
- USNあり媒体は未接続期間をUSN回収。
- USNなし・非NTFSで欠落時は全数照合要求。
- USNを作成・変更しない。
- PC時計補正しない。
- 同時複数PC書込みは対象外として検出時警告。

## テスト

- PC-AからPC-B引継ぎ。
- 同一イベント重複。
- 分岐。
- 媒体複製。
- manifest片側破損。
- segment途中抜去。
- ログディレクトリ削除。
- readonly媒体。
- FAT/exFAT品質。
- USNあり外付け回収。
- 異なるFormat version。
- path traversal防止。

## 受け入れ条件

- 確定履歴を後から書換えない。
- 別PCの同アプリで全確定ログを引き継げる。
- 同アプリがないPCの変更はUSNまたは照合品質で正直に表す。
- 媒体以外の履歴をミラーしない。
