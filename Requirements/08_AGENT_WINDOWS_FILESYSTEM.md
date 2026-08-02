# サブエージェント要件：Windows FileSystem Collector

## ブランチ

`feat/windows-filesystem`

## 所有パス

- `src/StorageChronicle.Platform.Windows.FileSystem/**`
- `tests/StorageChronicle.Platform.Windows.FileSystem.Tests/**`
- 対応する`docs/src/StorageChronicle.Platform.Windows.FileSystem/**`
- `docs/handoffs/windows-filesystem.md`

## 目的

ローカルボリュームの列挙・接続監視と、NTFS専用ジャーナルを利用できないファイルシステムの初期状態、接続中変更通知、Directory Reconciliationを実装する。

## 実装要件

- ローカルボリューム、ファイルシステム、ドライブ種別、Volume Identity、Volume GUIDパス、全マウントポイント、読取り可否を列挙する。ドライブ文字がなくてもディレクトリとして開けるボリュームを除外しない。
- 外付け媒体の接続・切断をイベント駆動で検出する。
- ディレクトリとして列挙できない領域を対象外として報告する。
- 非NTFSおよびUSN利用不能ボリュームはReadDirectoryChangesWの非同期監視を使い、ポーリングで全走査しない。
- 通知バッファーあふれ、監視ハンドル喪失、Agent停止期間、媒体強制取り外しをContinuity Gapとして報告する。
- 初期走査ではリンク・ジャンクション・リパースポイント自身を記録するがリンク先へ再帰しない。
- 初期走査ではアクセス可能な全項目の標準メタデータをディレクトリ単位のバッチ列挙で取得する。取得不能項目は最小品質で返す。
- 非NTFS初期走査では変更通知ハンドルを先に開始して境界Sequenceを記録し、走査中通知を上限付きで保持してスナップショットへ順番に適用する。通知欠落または上限超過時は初期状態をContinuous扱いせずContinuity Gapとして報告する。
- Directory Reconciliationはユーザーが確認ダイアログで実行を選んだ後だけ開始する。差分だけ返し、正確な変更時刻やプロセスを作らない。
- ReFSは能力検出し、永続ジャーナル連続性を保証できない場合は本Collectorの通知・照合品質へフォールバックする。
- Storage Chronicleデータ、標準除外、ユーザー除外をイベント生成前に適用する。ただし除外設定変更自体は設定履歴へ残す。
- キャンセル、アクセス拒否、消失、媒体切断をAgent全体へ波及させない。

## テスト

合成通知、バッファー欠落、初期走査中変更、初期走査通知上限超過、ドライブ文字なしVolume GUID、リパースループ、アクセス拒否、消失競合、FAT/exFAT品質、ReFS能力なし、接続・切断、除外、Directory Reconciliation、キャンセル。Windows実APIテストは特権カテゴリへ分離する。

## 受け入れ条件

- 非NTFS要件に実装担当が存在する。
- 接続中の通常監視で毎秒全走査しない。
- 欠落をContinuousとして扱わない。
- Windows 10 22H2で利用可能なAPIに限定するか能力検出する。
