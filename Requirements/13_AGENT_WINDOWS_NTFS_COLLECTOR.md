# サブエージェント要件：Windows NTFS Collector

## ブランチ

`feat/windows-ntfs`

## 所有パス

- `src/StorageChronicle.Platform.Windows.Ntfs/**`
- `tests/StorageChronicle.Platform.Windows.Ntfs.Tests/**`
- 対応する`docs/src/StorageChronicle.Platform.Windows.Ntfs/**`
- `docs/handoffs/windows-ntfs.md`

## 目的

ドライバーなしでNTFSの初期状態、USN継続監視、停止中回収、MFTベース高速照合、ETW相関を提供する。

## 実装要件

- P/Invokeを限定層へ閉じ込める。
- ボリューム列挙、接続・切断、非NTFS監視はWindows FileSystem Collectorへ委譲し、重複実装しない。
- `FSCTL_QUERY_USN_JOURNAL`。
- `FSCTL_READ_USN_JOURNAL`の待機読取り。
- `FSCTL_ENUM_USN_DATA`等の公開APIによるFile ID、親ID、名前、USN列挙。
- 必要な変更候補だけディレクトリ情報を取得。
- Journal IDと最終USN。
- Journal未作成時は作らない。
- Journal容量を変更しない。
- Journal切詰めとID変更を欠落として報告する。
- 初回MFT列挙後、アクセス可能な全項目の標準メタデータをディレクトリ単位でバッチ取得する。
- 初回列挙中に発生したUSNを後から適用する整合手順。
- ETW File I/OとProcess EventをCollector契約へ流す。
- Process品質Exact/Correlated/Unknown。
- ETW相関不能を不明にする。
- Windows 10 22H2能力検出。
- 取得不能ボリュームはAgent全体を停止しない。
- Reparse Pointを再帰しない。
- アクセス拒否、媒体取り外し、キャンセルを処理する。
- Source Eventバッファーをストリーム処理し、全件保持しない。
- 生`$MFT`セクター解析をしない。

## 全数照合

- MFT軽量列挙。
- 保存状態とのFile ID、親、名前、最終USN比較。
- 変更候補だけ詳細取得。
- 結果は通常イベントでなくReconciliation候補として返す。
- 照合開始はApplication側のユーザー選択後だけ。
- OS低優先度I/Oを使用する。

## テスト

- バイナリ構造体Layout。
- 合成USN buffer parse。
- rename old/new pairing。
- File ID再利用防止情報。
- Journal ID変更。
- 範囲切詰め。
- cancellation。
- malformed record。
- Windows privileged実テスト。
- Windows 10 API能力テスト定義。
- 大規模列挙の割当て量。

## 受け入れ条件

- 通常時に毎秒全走査しない。
- USN連続時は未取得分だけ読む。
- 公開APIだけでMFT高速照合する。
- Journalを変更しない。
- ドライバー追加時に同じCollector契約へ差替え可能。
