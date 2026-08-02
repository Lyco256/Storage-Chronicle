# feat/settings handoff

## 変更

- `StorageChronicle.Settings` を追加し、Machine/User Settings の version 1 JSON schema、既定値、全項目の範囲/absolute path 検証を実装。
- UTF-8（BOMなし）、一時ファイルの stable flush、同一ディレクトリ atomic replace、直前有効版 `.bak` 保持、schema 0 読み替え、未知フィールド無視、片側/両側破損からの既定値復旧を実装。
- `AgentSettingsService` を追加し、IPC gateway、Machine 権限検証、SettingsChanged 相当の履歴イベント（scope/時刻/変更項目のみ）、監視の安全な再起動状態を実装。
- `StorageChronicle.UI.Settings` を追加し、Agent gateway だけを利用するモーダル設定ダイアログ ViewModel を実装。UI の直接ファイル書込みはない。
- Settings/UI Settings の csproj、Headless テスト、`docs/src` のファイル対応文書を追加。

## 変更していない範囲

共有契約、Domain、既存 UI shell、Requirements、`StorageChronicle.slnx` は変更していない。`StorageChronicle.slnx` への project entry 追加は統合担当が行う共有ファイル更新である。

## 検証コマンドと結果

- `dotnet restore src/StorageChronicle.Settings/StorageChronicle.Settings.csproj` — 成功
- `dotnet restore src/StorageChronicle.UI.Settings/StorageChronicle.UI.Settings.csproj` — 成功
- `dotnet restore tests/StorageChronicle.Settings.Tests/StorageChronicle.Settings.Tests.csproj` — 成功
- `dotnet restore tests/StorageChronicle.UI.Settings.Tests/StorageChronicle.UI.Settings.Tests.csproj` — 成功
- `dotnet build src/StorageChronicle.Settings/StorageChronicle.Settings.csproj --no-restore` — 成功、警告 0
- `dotnet build src/StorageChronicle.UI.Settings/StorageChronicle.UI.Settings.csproj --no-restore` — 成功、警告 0
- `dotnet build tests/StorageChronicle.Settings.Tests/StorageChronicle.Settings.Tests.csproj --no-restore` — 成功、警告 0
- `dotnet build tests/StorageChronicle.UI.Settings.Tests/StorageChronicle.UI.Settings.Tests.csproj --no-restore` — 成功、警告 0
- `dotnet test tests/StorageChronicle.Settings.Tests/StorageChronicle.Settings.Tests.csproj --no-restore` — 12/12 成功
- `dotnet test tests/StorageChronicle.UI.Settings.Tests/StorageChronicle.UI.Settings.Tests.csproj --no-restore` — 4/4 成功

## 統合時の注意

`StorageChronicle.slnx` に次の 4 プロジェクトを追加し、統合後に solution 全体の復元/ビルド/テストを実行すること。

- `src/StorageChronicle.Settings/StorageChronicle.Settings.csproj`
- `src/StorageChronicle.UI.Settings/StorageChronicle.UI.Settings.csproj`
- `tests/StorageChronicle.Settings.Tests/StorageChronicle.Settings.Tests.csproj`
- `tests/StorageChronicle.UI.Settings.Tests/StorageChronicle.UI.Settings.Tests.csproj`

## 既知の制約

Agent の実 IPC transport 自体は共有 IPC 契約の担当範囲であり、この branch では `IAgentSettingsGateway` 境界として分離している。Linux 用 XDG path provider は将来の platform 実装で `ISettingsPathProvider` を差し替える。
