# Phase 0 トップCodex基盤要件

## 1. 目的

サブエージェントが共有ファイルを編集せず並列実装できる、コンパイル可能でテスト可能な最小基盤を`devenv`に作る。機能本体を先回りして実装しない。

## 2. 作成するもの

### 2.1 リポジトリ基盤

- ルート構成。
- `StorageChronicle.slnx`。
- `global.json`。
- 中央パッケージ管理。
- 共通ビルド設定。
- `.editorconfig`、Git属性、ignore。
- 初期パッケージに存在する`AGENTS.md`を検査し、要件と矛盾させず保持する。
- build/testスクリプトの動く骨格。
- 全プロジェクトとテストプロジェクト。
- `build/quality/**`と`build/package/**`の所有境界。
- `tests/StorageChronicle.Integration.Tests/Agent/**`と`CrossModule/**`の所有境界。
- 依存関係。
- Architecture Testの初期ゲート。
- Doc Mirror Validatorの最小実装と専用テストプロジェクト。
- `docs/architecture/system-overview.md`、`module-dependencies.md`、`event-pipeline.md`、`state-and-storage-model.md`。
- 文書要件で列挙された初期ADR。

### 2.2 安定契約

次を完全に定義し、サブエージェント開始後はトップCodex所有とする。

- 強く型付けしたID。
- Event Schema Version。
- Source Event Envelope。
- Canonical Operation。
- Canonical Event。
- Event Origin。
- Event Timeと連番。
- Event Time Quality。
- Metadata Quality。
- Process Attributionと品質。
- Monitoring Continuity。
- File MetadataとDelta。
- Directory Entry Relation。
- Reconciliation Gap。
- Mount Session。
- History Branch参照。
- Operation Icon意味ID。
- Collector、Normalizer、Store、State、Projection、Settings、IPCのインターフェース。

契約は現在の製品要件を表せる必要がある。機能エージェントに契約設計を残さない。

### 2.3 Fakeとfixture

- 決定的なFake Clock。
- Fake Source Event Collector。
- In-memory Event Store。
- 小規模サンプルイベント列。
- Event StackとDiff Viewが表示できるデザイン時データ。
- `src/StorageChronicle.Contracts/Runtime/**`をAgentエージェント専用実装領域として作成し、契約定義領域と分離する。
- Golden Fixture読込み契約。
- Temporary Directory helper。
- Fault Injection用抽象化。

### 2.4 UI基盤

トップCodexは`UI.Shared`と`UI.Desktop`のシェルだけ作る。

- Event StackとDiff Viewのトップナビゲーション。
- Feature ViewをDIで登録する契約。
- Operation Icon Resolver契約。
- Material Icons組込み。
- 共通テーマ。
- 共通アイコンガターの列定義。
- 共通仮想化データソース契約。
- 全数照合ダイアログ契約と確定文面。
- Agent未接続時にFake Dataで起動できるシェル。
- Settings Featureを後から登録する契約と設定ダイアログのホスト。

Event Stack本体とDiff View本体は実装しない。

## 3. プロジェクト参照

全参照をPhase 0で設定する。サブエージェントがproject referenceを追加しなくてよい状態にする。

## 4. パッケージ

`Requirements/01_ARCHITECTURE_AND_PROJECT_LAYOUT.md`のバージョンを中央管理し、各プロジェクトに必要なPackageReferenceをPhase 0で設定する。サブエージェントは中央バージョンを変更しない。

## 5. 基盤テスト

- 全プロジェクトがビルドする。
- Architecture Testが依存方向を検査する。
- IDと基本イベントのシリアライズ往復。
- IPCメッセージのバージョン拒否。
- Fake CollectorからFake Storeまでのsmoke test。
- Avalonia Desktop ShellのHeadless起動。
- Doc Mirror Validator自身のテスト。
- Windowsプロジェクトが非Windows CIでビルド対象から適切に分離される。

## 6. 禁止

- 実USN処理。
- 実SQLiteスキーマ。
- 完成状態エンジン。
- 完成Projection。
- 完成Event Stack。
- 完成Diff View。
- 完成Agentサービス。
- インストーラー。
- 先行してサブエージェント所有パスへ実装を置くこと。

## 7. 受け入れ条件

- クリーンcheckoutからbuild scriptが成功する。
- fast testが成功する。
- Desktop ShellがFake Dataで起動する。
- 全サブエージェントの所有パスが存在し、READMEまたはplaceholderだけである。
- 共有契約が製品要件の全イベントを表現できる。
- 各部分要件が参照する型とインターフェースがコンパイル可能。
- Source EventからCanonical Eventへの変換表、設定スキーマ、非NTFS Collector契約が各担当要件だけで実装可能。
- `docs/src`ミラー基盤が機能する。
- 基盤コミット後のworking treeがクリーン。
