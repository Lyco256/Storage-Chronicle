# サブエージェント要件：Agent Service・Application・IPC

## ブランチ

`feat/agent-ipc`

## 開始条件

Wave 1が`devenv`へ統合済みであること。

## 所有パス

- `src/StorageChronicle.Application/**`
- `src/StorageChronicle.Agent/**`
- `src/StorageChronicle.Contracts/Runtime/**`
- `tests/StorageChronicle.Agent.Tests/**`
- `tests/StorageChronicle.Integration.Tests/Agent/**`
- 対応する`docs/src`
- `docs/handoffs/agent-ipc.md`

共有契約定義そのものは変更しない。

## 目的

FileSystem Collector、NTFS Collector、Session Source、Normalizer、Settings、Storage、State、ProjectionをWindows Service内で安全に接続し、UIとSession AgentへNamed Pipe APIを提供する。

## 実装要件

- LocalSystem Windows Service。読取り可能な保護領域の列挙で必要な場合だけ`SeBackupPrivilege`を処理単位で有効化し、常時有効化しない。権限取得不能は対象ボリュームの品質へ反映し、Agent全体を停止しない。
- UIなしコンソール診断モードを開発時のみ提供。
- CollectorごとのSupervisor。
- 一Collector失敗で全Agentを落とさない。
- bounded channel。
- backpressureと欠落記録。
- Source保存後に`StorageChronicle.Normalization`でCanonical化し、Canonical保存後に状態更新。
- 読取り系ETWイベントはNormalizerの上限付き相関バッファーへ渡し、永続ログへ保存しない。
- Machine Settingsの読込み、検証済み変更適用、設定履歴イベント。
- 受信順、Source Sequence、重複処理。
- flush初期5秒、設定範囲1秒から60秒。
- 優先flushイベント。
- 容量不足でRecordingStopped。
- 容量回復後にUSN回収または全数照合要求。
- 欠落時に全数照合確認要求をUIへ送る。UI未接続時は要求と欠落区間を正本ログへPendingReconciliationRequestとして保存し、次回UI接続時に一度だけ提示する。
- ユーザーが実行しない場合UnverifiedGap。
- 手動照合APIを公開しない。
- Named Pipeはローカル専用とし、4バイトlittle-endian payload長とUTF-8 JSON payloadからなるフレームを使う。
- JSONはSystem.Text.Jsonのsource-generated serializerを使い、protocol major/minorを全メッセージへ含める。
- 一メッセージ上限は8MiBとし、一覧とLive更新はページまたはバッチへ分割する。
- Agent pipeのDACLはSYSTEMとAdministratorsを完全制御、Authenticated Usersを接続・読取り・通常設定要求に限定する。管理者限定設定はpipe impersonationでAdministrators所属を検査する。
- Session Agent入力は接続クライアントSIDと対象ログオンSession IDを検証し、Clipboard候補メッセージ以外を拒否する。
- UIは読取りと設定変更のみ。
- Session AgentはClipboard候補送信だけ許可。
- Agentクラッシュ回復設定。
- graceful shutdown。
- 保存先変更は安全な停止・再開手順。
- ファイル内容を開かない。

## UI接続

- Event Stackページ取得。
- Diff Projection取得。
- Live更新。
- フィルター保存。
- Volume品質。
- 全数照合ダイアログ要求と回答。
- Agent状態、記録停止、容量不足。

## テスト

- Fake Collector E2E。
- Collector障害。
- bounded channel満杯。
- Source保存失敗。
- Storage再起動。
- UI切断再接続。
- Session Agent偽装拒否。
- IPCバージョン不一致。
- 全数照合Yes/No。
- サービス停止。
- 5秒flushで最大未保存範囲。
- PID再利用。

## 受け入れ条件

- UI終了後も記録。
- Agent再起動後に正本ログとUSNから継続。
- 失敗を品質状態として残す。
- 無制限メモリキューがない。
