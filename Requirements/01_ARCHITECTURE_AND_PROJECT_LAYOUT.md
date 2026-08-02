# アーキテクチャとプロジェクト構成

## 1. 採用アーキテクチャ

全体は、モジュラーモノリス、Ports and Adapters、追記イベントログと再構築可能Projectionの組合せとする。MVVMはAvalonia UI層だけに適用する。

Plain MVVMを全体アーキテクチャにしない。ファイル監視、状態再構成、永続化、OS固有処理、UIを分離する。

## 2. 依存方向

- Domainは他のアプリプロジェクトへ依存しない。
- ContractsはDomainの安定した識別子だけを参照する。
- Platform.AbstractionsはDomainへ依存する。
- NormalizationはDomainとPlatform.Abstractionsへ依存する。
- SettingsはDomainとContractsへ依存する。
- StateはDomainへ依存する。
- ProjectionはDomainとStateへ依存する。
- StorageはDomainとApplicationの永続化ポートへ依存する。
- Windows FileSystem CollectorとWindows NTFS CollectorはDomainとPlatform.Abstractionsへ依存する。
- ApplicationはDomain、Platform.Abstractions、永続化ポートを組み合わせる。
- AgentはApplication、Storage、Windows Collector、Contractsを組み立てる。
- UIはDomain、Projection、Settings、Contracts、UI.Sharedへ依存し、StorageまたはWindows Collectorを直接参照しない。
- Session AgentはContractsとWindows Session Collectorへ依存する。
- ExternalMediaはDomain、Applicationポート、Storageの公開ポートへ依存する。

循環依存を禁止し、ArchUnitNETテストで検査する。

## 3. ルート構成

```text
/
├─ AGENTS.md
├─ README.md
├─ StorageChronicle.slnx
├─ global.json
├─ Directory.Build.props
├─ Directory.Build.targets
├─ Directory.Packages.props
├─ .editorconfig
├─ .gitattributes
├─ .gitignore
├─ src/
├─ tests/
├─ benchmarks/
├─ build/
├─ docs/
├─ Requirements/
├─ tools/
├─ installer/
└─ assets/
```

## 4. `src`構成

```text
src/
├─ StorageChronicle.Domain/
├─ StorageChronicle.Contracts/
├─ StorageChronicle.Platform.Abstractions/
├─ StorageChronicle.Application/
├─ StorageChronicle.Normalization/
├─ StorageChronicle.Settings/
├─ StorageChronicle.State/
├─ StorageChronicle.Projection/
├─ StorageChronicle.Storage/
├─ StorageChronicle.Platform.Windows.FileSystem/
├─ StorageChronicle.Platform.Windows.Ntfs/
├─ StorageChronicle.Platform.Windows.Session/
├─ StorageChronicle.ExternalMedia/
├─ StorageChronicle.Agent/
├─ StorageChronicle.SessionAgent/
├─ StorageChronicle.UI.Shared/
├─ StorageChronicle.UI.EventStack/
├─ StorageChronicle.UI.DiffView/
├─ StorageChronicle.UI.Settings/
└─ StorageChronicle.UI.Desktop/
```

### 4.1 Domain

安定したOS非依存モデルを持つ。

- 強く型付けしたID。
- Source Event Envelope。
- Canonical Event。
- ファイル状態とメタデータ差分。
- 時刻と品質。
- Process Attribution。
- Mount Sessionと履歴ブランチの値。
- Operation Iconの意味ID。
- スキーマバージョン。

UI、SQLite、Avalonia、Windows API型を含めない。

### 4.2 Contracts

プロセス間通信DTOとバージョン交渉を持つ。内部Domain型をそのままNamed Pipeへ流さず、4バイトlittle-endian長＋UTF-8 JSON、System.Text.Json source generation、protocol major/minorによる互換形式を定義する。

### 4.3 Platform.Abstractions

- `ISourceEventCollector`
- `IVolumeEnumerator`
- `IVolumeSnapshotReader`
- `IProcessEventSource`
- `IClipboardEventSource`
- `IShareStateSource`
- `IPlatformCapabilities`
- 将来のDriver Collector契約

### 4.4 Application

収集、正規化、保存、状態更新、品質管理、照合要求、UI通知を調停するユースケースとポートを持つ。

### 4.5 Normalization

Source EventをCanonical Eventへ決定的に変換する。読取り専用I/Oは永続履歴へ保存せず、プロセス相関等の短期情報にだけ使用する。USN、ETW、Clipboard、Share、Cloud、Reconciliationの正規化と品質付与を担当する。

### 4.6 Settings

OS非依存の設定スキーマ、検証、バージョン移行を持つ。サービス側Machine SettingsとUI側User Settingsを分離し、設定変更を履歴イベントとして記録できる契約を提供する。

### 4.7 State

File ID、親関係、名前、メタデータの期間付き状態を管理する。任意シーケンス・時刻の状態を取得する。フォルダー移動・削除で子孫イベントを展開保存しない。

### 4.8 Projection

- Event Stack Source/Normalized/Grouped。
- Activity Group。
- Diff View Live/Period/Point-in-Time/Replay。
- TreeとExplorer共通のFile System Projection。
- 主操作色、サブアイコン、表示ルート。
- フィルターとページング。

### 4.9 Storage

- 追記専用イベントセグメント。
- Zstandard圧縮。
- セグメントチェックサム。
- SQLite索引。
- 状態スナップショット。
- 破損検出。
- SQLite再構築。
- 容量不足停止。

EF Coreは使用しない。Microsoft.Data.Sqliteを直接使用し、SQL、トランザクション、索引を明示する。

### 4.10 Windows FileSystem

- ローカルボリューム列挙と接続・切断。
- NTFS以外の初期ディレクトリ走査。
- ReadDirectoryChangesWによる接続中監視。
- 通知欠落検出。
- 非NTFSのDirectory Reconciliation。
- 標準除外とユーザー除外の適用。
- ReFS能力検出と利用可能機能へのルーティング。

### 4.11 Windows NTFS

- Windows FileSystem Collectorが列挙したNTFSボリュームへの接続。
- USN Journal照会。
- USN待機読取り。
- MFT相当の公開API列挙。
- Journal ID連続性。
- 高速Reconciliation。
- ETW File I/Oとプロセスイベント。
- Windows 10能力検出。

生`$MFT`セクター解析を正本にしない。

### 4.12 Windows Session

- ログオンユーザーセッション内クリップボード通知。
- Explorer Clipboard Generation。
- Session Agent側IPC。
- SMB共有スナップショットと変化。
- OneDrive等のプレースホルダー状態取得補助。

### 4.13 ExternalMedia

- Volume Identity。
- Mount Session。
- 媒体ミラー。
- 不変セグメントとマニフェスト。
- ブランチ分岐。
- 別PCログ取込み。
- 突然取り外し復旧。

### 4.14 Agent

Windows Serviceホスト、DI、Collectorのライフサイクル、保存パイプライン、Named Pipeサーバー、停止・復旧を担当する。

### 4.15 UI

- `UI.Shared`：テーマ、共通ガター、仮想化契約、ナビゲーション、ダイアログ契約、Operation Icon解決。
- `UI.EventStack`：3表示モード。
- `UI.DiffView`：共通ProjectionからTree/Explorer。
- `UI.Settings`：設定パネル、検証、Agentへの設定変更要求。
- `UI.Desktop`：トップレベルシェル、依存注入、2画面の切替、Agent接続。

## 5. `tests`構成

```text
tests/
├─ StorageChronicle.Domain.Tests/
├─ StorageChronicle.Normalization.Tests/
├─ StorageChronicle.Settings.Tests/
├─ StorageChronicle.State.Tests/
├─ StorageChronicle.Projection.Tests/
├─ StorageChronicle.Storage.Tests/
├─ StorageChronicle.Platform.Windows.FileSystem.Tests/
├─ StorageChronicle.Platform.Windows.Ntfs.Tests/
├─ StorageChronicle.Platform.Windows.Session.Tests/
├─ StorageChronicle.ExternalMedia.Tests/
├─ StorageChronicle.Agent.Tests/
├─ StorageChronicle.UI.EventStack.Tests/
├─ StorageChronicle.UI.DiffView.Tests/
├─ StorageChronicle.UI.Settings.Tests/
├─ StorageChronicle.DocMirrorValidator.Tests/
├─ StorageChronicle.Architecture.Tests/
├─ StorageChronicle.Integration.Tests/
├─ StorageChronicle.WindowsIntegration.Tests/
├─ StorageChronicle.UI.Headless.Tests/
├─ StorageChronicle.EndToEnd.Tests/
└─ StorageChronicle.Installer.Tests/
```

## 6. その他

```text
benchmarks/
└─ StorageChronicle.Benchmarks/

tools/
├─ StorageChronicle.DocMirrorValidator/
├─ StorageChronicle.TestDataGenerator/
└─ StorageChronicle.LogInspector/

docs/
├─ architecture/
├─ decisions/
├─ handoffs/
├─ release/
├─ benchmarks/
└─ src/
```

## 7. 技術バージョン

2026-08-02時点の安定版として中央管理する。

- Target Framework：`net10.0`。
- Avalonia：`12.1.1`。
- Avalonia.Desktop：`12.1.1`。
- Avalonia.Themes.Fluent：`12.1.1`。
- Avalonia.Fonts.Inter：`12.1.1`。
- Avalonia.Headless.XUnit：`12.1.1`。
- CommunityToolkit.Mvvm：`8.4.2`。
- Material.Icons.Avalonia：`3.0.2`。
- Microsoft.Extensions.Hosting：`10.0.10`。
- Microsoft.Extensions.Hosting.WindowsServices：`10.0.10`。
- Microsoft.Data.Sqlite：`10.0.10`。
- System.IO.Hashing：`10.0.10`。
- ZstdSharp.Port：`0.8.8`。
- xunit.v3：`3.2.2`。
- TngTech.ArchUnitNET.xUnitV3：`0.13.3`。
- BenchmarkDotNet：`0.15.8`。
- Microsoft.Testing.Extensions.CodeCoverage：`18.9.0`。
- WixToolset.Sdk：`6.0.2`（installer project only）。

プレビュー版を使用しない。全パッケージバージョンは`Directory.Packages.props`だけで管理する。

## 8. ビルド設定

- Nullable有効。
- ImplicitUsings有効。
- TreatWarningsAsErrors有効。
- Deterministic build有効。
- ContinuousIntegrationBuildはCI時有効。
- AnalysisLevelは利用SDKの`latest-recommended`。
- 生成コード以外の警告抑止を原則禁止。
- C#言語バージョンは`latestMajor`ではなく.NET 10 SDK既定に固定する。
- CPUターゲットはWindows実行プロジェクトでx64。
- OS非依存ライブラリは`net10.0`。
- Windows APIプロジェクトは`net10.0-windows10.0.19041.0`。
- UIはWindows/Linux共通の`net10.0`を維持し、Windows固有処理を直接参照しない。

## 9. Native AOT

AgentとSession Agentについて、通常発行を正本とする。基盤にAOT互換性検査用発行設定を用意するが、MVP受け入れ条件にNative AOT成功を含めない。AOTのために機能、診断、互換性を落とさない。
