# サブエージェント要件：Windows Session・Clipboard・Share

## ブランチ

`feat/windows-session-share`

## 所有パス

- `src/StorageChronicle.Platform.Windows.Session/**`
- `src/StorageChronicle.SessionAgent/**`
- `tests/StorageChronicle.Platform.Windows.Session.Tests/**`
- 対応する`docs/src/StorageChronicle.Platform.Windows.Session/**`
- 対応する`docs/src/StorageChronicle.SessionAgent/**`
- `docs/handoffs/windows-session-share.md`

## 目的

ユーザーセッション内クリップボード候補、Explorerコピー相関用情報、SMB共有状態、Cloud Placeholder補助を取得する。

## クリップボード

- 非表示ウィンドウでクリップボード変更通知を受ける。
- ポーリングしない。
- CF_HDROP等からファイルパス一覧。
- copy/cut意図。
- Clipboard Generation ID。
- 内容変更まで候補保持。
- 同Generation複数貼付け。
- 貼付けと関連しない候補を永続変更ログへ保存しない。
- クリップボードがロック中なら短い非ブロッキング再試行。
- クリップボード内容自体を長期保存しない。
- Session AgentからAgentへバージョン付きIPC。
- Explorer Copy HookとExplorer DLLを実装しない。

## 相関用出力

このエージェントはコピー確定を単独で行わず、Applicationへ候補を渡す。品質：

- ConfirmedIntent。
- CorrelatedSource。
- SourceUnknown。
- NotIdentified。

## Share

- Agent起動時にSMB共有全数取得。
- 共有名、ローカルパス、種別、説明、共有権限。
- `RegNotifyChangeKeyValue`でLanmanServer共有設定の変更を待機し、通知後に`NetShareEnum`スナップショットを再取得して差分化する。通知登録が利用不能な場合だけ30秒間隔の`NetShareEnum`へフォールバックし、品質へFallbackPollingを記録する。
- Share Eventをファイルメタデータ変更と分離。
- リモートアクセス読取り履歴を取らない。
- リモートPC・ユーザーを取らない。
- 共有経由実編集はNTFS Collector側の通常変更。

## Cloud Placeholder

- ローカルプレースホルダー状態を能力検出。
- Hydrated、Dehydrated、Placeholder Metadata変化を区別できるSource情報。
- クラウドAPI、クラウド履歴、ユーザー情報を取得しない。

## テスト

- クリップボードGeneration。
- 同内容複数貼付け。
- clipboard lock。
- cut/copy。
- CF_HDROPなし。
- IPC切断。
- Share snapshot diff。
- Share permission change。
- Windows 10能力検出。
- Cloud API不在時の安全な無効化。

## 受け入れ条件

- Session 0サービスから直接クリップボードを読まない。
- ユーザーセッションプロセスのメモリ常駐を抑える。
- 不明なコピーを確定コピーとして返さない。
- Share変更は専用イベント。
