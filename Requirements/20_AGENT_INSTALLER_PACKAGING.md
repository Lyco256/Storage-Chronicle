# サブエージェント要件：Installer・Packaging

## ブランチ

`feat/installer-packaging`

## 開始条件

Wave 2統合済みでAgent、Session Agent、UIが動作すること。

## 所有パス

- `installer/**`
- `build/package/**`
- `tests/StorageChronicle.Installer.Tests/**`
- `docs/installer/**`
- `docs/build/package/**`
- `docs/handoffs/installer-packaging.md`

## 目的

一つの通常WindowsインストーラーとしてUI、Agent Service、Session Agentを導入し、アンインストールと更新を安全に行う。

## 実装要件

- WiX Toolset SDK `6.0.2`でMSIを作成し、要件変更なしに別メジャーバージョンへ更新しない。

- Windows 10 22H2 x64とWindows 11 x64。
- 一つの製品として表示。
- AgentをLocalSystem自動起動サービスとして登録。
- サービス回復：5秒、15秒、60秒。
- Session Agentをユーザーログオン時に起動。
- UIは通常ユーザー権限。
- 標準データを`ProgramData`へ作成。
- アンインストール時、履歴データを既定で削除しない。
- 履歴データ削除オプションを付けない。
- 更新時にAgentを安全停止、ファイル置換、再起動。
- インストール失敗時にサービス半登録を残さない。
- ドライバーを含めない。
- UI、Agent、Session AgentをWindows x64 self-containedで発行し、別アプリや.NETランタイムを手動インストールさせない。
- コード署名なし開発ビルドと将来署名済みリリースの手順を分ける。
- SmartScreen回避のために危険な挙動やセキュリティ低下を行わない。

## テスト

- clean install。
- repair/update。
- uninstall。
- history retained。
- service recovery設定。
- Session Agent起動。
- 非管理者UI。
- 保存先権限。
- Windows 10用パッケージ定義。
- ロールバック。

## 受け入れ条件

- 一つのインストーラーで完結。
- 別製品の常駐アプリへ依存しない。
- アンインストールでユーザーログを消さない。
- ドライバー関連設定を変更しない。
