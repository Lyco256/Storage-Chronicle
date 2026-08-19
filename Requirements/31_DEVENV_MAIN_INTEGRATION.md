# devenv・main最終統合要件

## 1. 目的

全最終受入れ項目を完了したcommitを統合ブランチ`devenv`へ集約し、全ゲート通過後にstable `main`を作る。

## 2. 作業開始時のbranch監査

トップCodexだけが実行する。

必ず次を保存する。

- `git status`
- `git branch -vv`
- `git branch -a`
- `git log --graph --decorate --oneline --all`
- remote refs
- current HEAD SHA
- clean/dirty state

remoteに`devenv`または`main`が見えなくてもローカルrefを確認せず作成してはならない。

## 3. 既存devenvがある場合

既存`devenv`を正本integration branchとする。

各最終feature branchはレビュー・Fast/targeted test通過後、トップCodexが`devenv`へ`--no-ff`でmergeする。

subagentはmergeしない。

## 4. devenvがどこにも存在しない場合

履歴を推測して古いcommitから`devenv`を再構築しない。

現在の最終受入れ作業開始基準branchが、既存実装をすべて含む唯一の継続branchであることをgit graphで確認する。

確認できた場合だけ、そのaccepted HEADを`devenv`のbootstrap pointとする。この一回だけmerge commitを人工的に作らずbranch refを作成してよい。

`docs/release/branch-bootstrap.md` に次を記録する。

- なぜbootstrapが必要だったか
- source branch
- source SHA
- existing branch refs
- history graph
- userの当初branch規則へ戻ること

以降の作業は通常の`devenv` integration規則へ戻す。

## 5. main

`main`は最終ゲート前に作らない。

`devenv`で以下がすべてPASSした後だけ作成・更新する。

- build
- Test-Fast
- Test-All
- DocMirrorValidator
- coverage
- Windows privileged matrix
- confirmed reconciliation
- NTFS candidate/privilege/low-I/O acceptance
- current 600s resource acceptance
- full benchmark matrix including MFT
- Windows 11 installer real-machine acceptance
- Windows 10 22H2 physical acceptance
- Agent/Explorer real correlation measurement
- requirements verificationに未完了blocking rowなし

`main`が存在しない場合はaccepted `devenv` HEADから作成する。存在する場合は履歴を消さず、通常のmergeで統合する。

force push、rebase、history rewrite、squashで履歴を破壊しない。

## 6. GitHub remote

ブランチをpush後、remoteが現在`feat/*`をdefaultにしている場合、最終安定版では`main`をdefault branchにする。

Codexが認証済み`gh`で安全に変更できるなら実行してよい。認証または権限がなければ、ユーザーへGitHub Web UIでDefault branchを`main`へ変更する操作だけ案内する。

## 7. 最終タグ

このフェーズではユーザーが明示していないrelease tagを勝手に作らない。

## 8. 完了条件

- remote `devenv`存在
- remote `main`存在
- `main`が全最終受入れを通過したSHAを指す
- feature branchの未統合必要変更なし
- worktree clean
- main-readinessにblocking itemなし
- remote default branchが`main`、またはユーザーに最後のWeb UI操作を明確に案内済み
