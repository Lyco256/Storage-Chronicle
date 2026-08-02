# Storage Chronicle トップCodex要件

## 1. 役割

トップCodexは、ユーザーと直接会話する唯一の統合責任者である。次を行う。

1. 初期リポジトリ状態を検査する。
2. `main`、`devenv`、`feat/*`のブランチ規則を確立する。
3. サブエージェントが競合せず実装できる基盤コードと共有契約を先に作る。
4. 機能ごとにサブエージェントとworktreeを作成する。
5. 各サブエージェントの変更、テスト結果、文書、担当外変更の有無をレビューする。
6. 合格したブランチだけを`devenv`へマージする。
7. 統合テスト、性能テスト、障害テストを実行し、統合不具合はトップCodexが修正する。
8. 全受け入れ条件を満たした時だけ`devenv`を`main`へマージする。

Codexのサブエージェントとworktreeは、限定された作業を並列化し、各作業ディレクトリを分離するために使う。サブエージェントへ曖昧な設計判断を委ねてはならない。

## 2. 作業開始前の確認

次を実行し、結果を記録する。

- 現在ブランチが`devenv`であること。
- 作業ツリーがクリーンであること。
- 現在のHEADを開始コミット`S`として記録すること。
- `main`が存在しない場合、`S`を指す`main`を作成すること。
- `main`が存在する場合、履歴を書き換えず、`devenv`が`main`から派生していることを確認すること。
- 不整合がある場合はコードを変更せずユーザーへ報告すること。
- force push、履歴改変、既存コミットのrebaseを行わないこと。

## 3. 進行手順

### Phase 0：トップCodex基盤作成

`Requirements/05_FOUNDATION_PHASE.md`を実装する。基盤が全ビルド・全基礎テストを通るまでサブエージェントを開始しない。

基盤完了後、`devenv`へ次の形式でコミットする。

`chore(foundation): establish Storage Chronicle architecture contracts`

### Wave 1：並列実装

基盤コミットから各`feat/*`ブランチとworktreeを作る。

| 要件 | ブランチ |
|---|---|
| 07 | `feat/normalization` |
| 08 | `feat/windows-filesystem` |
| 09 | `feat/settings` |
| 10 | `feat/state-engine` |
| 11 | `feat/projection-grouping` |
| 12 | `feat/storage-engine` |
| 13 | `feat/windows-ntfs` |
| 14 | `feat/windows-session-share` |
| 15 | `feat/ui-event-stack` |
| 16 | `feat/ui-diff-view` |

各サブエージェントへ次を明示して渡す。

- 共通要件ファイル一覧
- 担当要件ファイル
- 担当ブランチ
- 担当worktree
- 所有パス
- 禁止パス
- 完了時に必要なテストコマンド
- 完了報告の形式

Wave 1のブランチは、トップCodexが一つずつレビューして`--no-ff`で`devenv`へマージする。マージ順は次とする。

1. normalization
2. windows-filesystem
3. settings
4. state-engine
5. projection-grouping
6. storage-engine
7. windows-ntfs
8. windows-session-share
9. ui-event-stack
10. ui-diff-view

各マージ後に高速テストを実行する。Wave 1完了後に全単体テスト、アーキテクチャテスト、Headless UIテストを実行する。

### Wave 2：統合依存実装

Wave 1統合済み`devenv`から作る。

| 要件 | ブランチ |
|---|---|
| 17 | `feat/agent-ipc` |
| 18 | `feat/external-media` |

2ブランチは並列でよい。レビュー後、`agent-ipc`、`external-media`の順に`devenv`へマージし、全テストを実行する。

### Wave 3：完成・品質保証

Wave 2統合済み`devenv`から作る。

| 要件 | ブランチ |
|---|---|
| 19 | `feat/integration-quality` |
| 20 | `feat/installer-packaging` |

統合品質ブランチを先にマージし、インストーラーを後にマージする。その後、Windows管理者権限テスト、外付け媒体テスト、性能測定、障害復旧テストを実行する。

## 4. サブエージェントの完了条件

トップCodexは、次を全て確認するまでマージしない。

- 担当要件を満たす。
- 担当パス以外を変更していない。
- コンパイル警告を増やしていない。
- 担当テストが全て成功する。
- 例外系・破損系・キャンセル系テストがある。
- `docs/src`の対応文書が存在する。
- 公開APIのXMLコメントがある。
- ハンドオフ文書に変更概要、テスト結果、既知の制限がある。
- 一時ファイル、生成物、秘密情報をコミットしていない。
- 共有契約の独自拡張や複製型を作っていない。

## 5. 共有契約変更

Phase 0で作成した次のパスはトップCodex所有である。

- `src/StorageChronicle.Domain/Contracts/**`
- `src/StorageChronicle.Contracts/**`のうち`Runtime/**`を除く契約定義
- `src/StorageChronicle.Platform.Abstractions/**`
- `src/StorageChronicle.UI.Shared/Contracts/**`
- `Directory.*`
- `global.json`
- `StorageChronicle.slnx`
- `AGENTS.md`

サブエージェントが変更を必要とした場合は、コードを迂回する複製型を作らず、`docs/handoffs/<branch>.md`へ具体的な変更要求を書く。トップCodexが必要性を判断して`devenv`へ契約変更を行う。進行中のfeature branchへ反映する必要がある場合、トップCodexだけが対象エージェントを停止し、`devenv`の契約変更コミットを対象feature branchへmergeしてから作業を再開させる。rebaseとサブエージェント自身による同期mergeは禁止する。

## 6. マージ競合対応

- 自動競合解決で両方を雑に残さない。
- どちらの要件が正しいか共通要件から判断する。
- Source Eventを失う解決、ログ形式を壊す解決、テスト削除による解決を禁止する。
- UI上の表示解釈と記録上の事実を混同しない。
- 競合解決後は当該機能のテストだけでなく全高速テストを実行する。

## 7. `main`への昇格条件

次を全て満たすまで`main`へマージしない。

- `dotnet build`が警告ゼロで成功する。
- 全単体・契約・アーキテクチャ・Headless UI・統合テストが成功する。
- Windows特権テストが対象環境で成功する。
- 追記ログ破損テストとSQLite再構築テストが成功する。
- 記録停止・USN欠落・全数照合拒否の各フローが成功する。
- Event Stackの3モードとDiff Viewの全主要モードが動作する。
- ドキュメントミラー検査が成功する。
- ログ削除機能が存在しない。
- ファイル内容または内容ハッシュを読む実装が存在しない。
- 定義済み測定条件でAgentとSession AgentのPrivate Working Set合計が50MiB未満、CPU平均が0.5%以下である。未達の場合はユーザーが例外を明示承認しない限り完了扱いにしない。
- Windows 10互換テスト定義が存在し、Windows 11専用APIは能力検出で隔離されている。Windows 10実機がない場合は`docs/release/windows10-compatibility.md`へ未実行項目を明示し、検証済みとは報告しない。
- `Requirements/98_REQUIREMENTS_COVERAGE.md`の全項目を実装結果へ再照合し、未達がない。
- 受け入れ結果を`docs/release/main-readiness.md`へ記録している。

`main`へのマージは`--no-ff`を使用し、タグはユーザーの明示指示があるまで作成しない。
