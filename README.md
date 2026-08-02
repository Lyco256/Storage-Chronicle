# Storage Chronicle Codex 実装パッケージ

このパッケージは、トップCodexが複数のサブエージェントとGit worktreeを使ってStorage Chronicleを段階的に実装するための要件一式である。


## このパッケージに最初から存在するファイル

初期ZIPには`AGENTS.md`、`TOP_CODEX.md`、`README.md`、`Requirements/*.md`だけを収録する。`StorageChronicle.slnx`、`global.json`、`Directory.*`、`src`、`tests`、`build`、`docs`、`tools`、`installer`、`benchmarks`は欠落ではなく、Phase 0でトップCodexが作成する成果物である。実装開始前に別途必要なテンプレート、JSON、バイナリ、外部リポジトリ内ファイルは存在しない。

## 読む順序

Codexはリポジトリを開くとルートの`AGENTS.md`を自動的に読み込む。トップCodexはその後、次を順番に読む。

1. `TOP_CODEX.md`
2. `Requirements/00_PRODUCT_REQUIREMENTS.md`
3. `Requirements/01_ARCHITECTURE_AND_PROJECT_LAYOUT.md`
4. `Requirements/02_GIT_BRANCH_WORKTREE_RULES.md`
5. `Requirements/03_TEST_AND_QUALITY_REQUIREMENTS.md`
6. `Requirements/04_DOCUMENTATION_REQUIREMENTS.md`
7. `Requirements/05_FOUNDATION_PHASE.md`
8. `Requirements/06_AGENT_OWNERSHIP_MATRIX.md`
9. `Requirements/98_REQUIREMENTS_COVERAGE.md`
10. `Requirements/99_TECHNICAL_REFERENCES.md`

基盤フェーズ完了後、トップCodexは各サブエージェントへ共通要件と担当ファイルを渡す。サブエージェントは自分の担当要件だけでなく、上記の共通要件も読む。

## サブエージェント実装波

### Wave 1：独立実装

- `07_AGENT_NORMALIZATION.md`
- `08_AGENT_WINDOWS_FILESYSTEM.md`
- `09_AGENT_SETTINGS.md`
- `10_AGENT_STATE_ENGINE.md`
- `11_AGENT_PROJECTION_GROUPING.md`
- `12_AGENT_STORAGE_ENGINE.md`
- `13_AGENT_WINDOWS_NTFS_COLLECTOR.md`
- `14_AGENT_WINDOWS_SESSION_AND_SHARE.md`
- `15_AGENT_UI_EVENT_STACK.md`
- `16_AGENT_UI_DIFF_VIEW.md`

### Wave 2：統合依存実装

Wave 1を`devenv`へ統合した後に開始する。

- `17_AGENT_AGENT_SERVICE_IPC.md`
- `18_AGENT_EXTERNAL_MEDIA.md`

### Wave 3：完成・品質保証

Wave 2を統合した後に開始する。

- `19_AGENT_INTEGRATION_QUALITY.md`
- `20_AGENT_INSTALLER_PACKAGING.md`

## 重要事項

- リポジトリの初期ブランチは`devenv`である。
- `main`は安定版、`devenv`は統合版、`feat/*`はサブエージェントの作業ブランチである。
- トップCodex以外は`main`と`devenv`を編集・マージしない。
- サブエージェントは担当外ファイルを変更しない。
- 共有契約に不足がある場合、サブエージェントは勝手に変更せず、担当ブランチの`docs/handoffs/<branch>.md`へ変更要求を書く。
- ソース変更には、同じ相対パスを持つ`docs/src`内の説明文書とテストを含める。
