# Gitブランチ・worktree規則

## 1. ブランチ階層

```text
main
└─ devenv
   ├─ feat/normalization
   ├─ feat/windows-filesystem
   ├─ feat/settings
   ├─ feat/state-engine
   ├─ feat/projection-grouping
   ├─ feat/storage-engine
   ├─ feat/windows-ntfs
   ├─ feat/windows-session-share
   ├─ feat/ui-event-stack
   ├─ feat/ui-diff-view
   ├─ feat/agent-ipc
   ├─ feat/external-media
   ├─ feat/integration-quality
   └─ feat/installer-packaging
```

初期チェックアウトは`devenv`である。`main`が存在しない場合、作業開始時の`devenv` HEADから作成する。基盤作業は`devenv`へコミットし、その後の機能ブランチは基盤コミット済み`devenv`から作る。

## 2. 権限

- トップCodexだけが`main`と`devenv`を変更する。
- サブエージェントは自分の`feat/*`だけを変更する。
- サブエージェントはマージ、rebase、force pushを行わない。
- トップCodexは`feat/*`を`devenv`へ`--no-ff`でマージする。
- `devenv`から`main`も`--no-ff`でマージする。

## 3. worktree

worktree名は次で固定する。

`../storage-chronicle-wt-<branch-slug>`

例：`../storage-chronicle-wt-state-engine`

一つのブランチを複数worktreeで同時にチェックアウトしない。サブエージェント終了後もトップCodexがレビューを終えるまでworktreeを削除しない。

## 4. 所有パス

各部分要件に所有パスを記載する。サブエージェントは次を禁止する。

- 部分要件で所有を明示されていない`src`、`tests`、`docs`、`build`、`tools`、`benchmarks`、`installer`の編集。
- `Directory.*`、`global.json`、solution、共通契約の編集。
- 他エージェントのプロジェクト参照追加。
- 同等型や同等インターフェースを別名前空間に複製すること。
- Requirementsの変更。

共有変更が必要な場合は`docs/handoffs/<branch-slug>.md`へ変更要求を記録する。トップCodexが`devenv`へ共有変更をコミットした後、進行中ブランチへの反映が必要なら、トップCodexだけが対象worktreeの作業を停止させて`devenv`をfeature branchへmergeする。サブエージェントによるmergeまたはrebaseは禁止する。

## 5. コミット

Conventional Commitsを使用する。

- `feat(state): ...`
- `feat(storage): ...`
- `test(ntfs): ...`
- `docs(diff-view): ...`
- `fix(agent): ...`

コミットはビルド可能な単位にする。テストを後回しにした未完成コミットを最終HEADにしない。生成物、`bin`、`obj`、TestResults、ベンチマーク成果物をコミットしない。

## 6. サブエージェントの最終状態

- working treeがクリーン。
- 全変更がコミット済み。
- 最終コミットメッセージが担当機能を示す。
- ハンドオフ文書がある。
- 実行したコマンドと結果を報告する。
- 未解決事項を隠さない。

## 7. マージ

トップCodexはマージ前に差分を確認し、所有外変更があればマージを拒否する。マージ競合が起きた場合、サブエージェントへ両方編集させず、トップCodexが要件と契約を基準に解決する。

マージ後は最低限次を実行する。

- `dotnet build StorageChronicle.slnx`
- 当該機能のテスト。
- Architecture Tests。
- Doc Mirror Validator。

Wave単位の最後には全高速テストを実行する。

## 8. バグ修正

統合後に見つかった機能内バグは、トップCodexが`fix/<slug>`を`devenv`から作るか、統合中だけトップCodexが`devenv`上で修正する。複数モジュールにまたがる修正はトップCodexが担当する。
