# サブエージェント要件：Settings

## ブランチ

`feat/settings`

## 所有パス

- `src/StorageChronicle.Settings/**`
- `src/StorageChronicle.UI.Settings/**`
- `tests/StorageChronicle.Settings.Tests/**`
- `tests/StorageChronicle.UI.Settings.Tests/**`
- 対応する`docs/src/StorageChronicle.Settings/**`
- 対応する`docs/src/StorageChronicle.UI.Settings/**`
- `docs/handoffs/settings.md`

## 目的

Machine SettingsとUser Settingsのスキーマ、検証、原子的保存、移行、設定UIを実装する。設定はトップレベル画面を増やさず、モーダル設定ダイアログとして提供する。

## Machine Settings

- 監視対象と除外パス。
- 標準ノイズフィルター。
- ログ保存先。
- 媒体ごとのミラー設定。
- flush間隔。初期値5秒、整数1秒から60秒だけを許可する。
- 変更はAgentのIPCを経由し、Agentが権限と値を検証して適用する。
- Windowsでは`C:\ProgramData\Storage Chronicle\config\machine-settings.json`へUTF-8 JSONのversioned schemaとして保存し、同一ディレクトリの一時ファイル、flush、atomic replaceで確定する。
- 直前の有効版を一世代保持する。両方破損時は既定値へ復旧して警告する。
- 適用成功後に設定変更時刻と変更項目を履歴イベントとして記録する。秘密値やファイル内容は存在しない。

## User Settings

- Activity GroupとPaneのタイムアウト。初期値は2秒と5秒、設定範囲は各0.5秒から60秒。
- Event Stackのページ行数、並び、初期モード。ページ行数は整数50から5000、初期値250。
- Diff Viewの表示形式、ズーム、枠並び。ズームは50%から300%、初期値100%。
- 保存済みフィルター。
- Windowsでは`%LOCALAPPDATA%\Storage Chronicle\user-settings.json`へMachine Settingsと同じ原子的方式で保存する。将来Linuxではプラットフォームパスプロバイダーを差し替え、`XDG_CONFIG_HOME`を使用する。

## UI

- 値の範囲とパスを保存前に検証する。
- Machine Settingsの適用失敗を成功表示しない。
- 全数照合を任意起動するボタン、設定、コマンドを作らない。
- 設定変更で監視再起動が必要な場合、Agentが安全な停止・flush・再開を実行し、UIは状態を表示する。

## テスト

既定値、全項目往復、schema移行、未知フィールド、片側破損、両側破損、atomic replace失敗、範囲検証、保存先変更、除外、ミラー、IPC拒否、Headless設定UI。

## 受け入れ条件

- 製品要件にある全設定の保存先と担当が明確である。
- 設定破損でAgentが起動不能にならない。
- UIが設定ファイルを直接書き換えない。
