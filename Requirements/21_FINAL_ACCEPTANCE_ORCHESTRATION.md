# 最終受入れフェーズ統括要件

> 2026-09-28方針変更: 仮想環境を作らない。以下の旧TestLab/VM実行指示より `37_PHYSICAL_READ_ONLY_ACCEPTANCE.md` の実機データ保護、監査、権限境界を優先する。

## 1. 目的

この文書は、製品の記録品質・安全性・機能要件を維持しつつ、2026-08-19 時点で未完了の最終受入れ項目を閉じるための追加要件である。実機試験方法は2026-09-28の方針変更と37に従う。

対象は次の9項目とする。

1. 確認済み再調整（confirmed reconciliation）の実処理
2. NTFS候補限定メタデータ取得、`SeBackupPrivilege`、低優先I/O
3. Windows特権機能マトリクス
4. Windows 10 22H2 x64 実環境受入れ
5. 正式なアイドルリソース受入れ
6. MFTを含む性能マトリクス
7. 実機インストーラー受入れ
8. Agent／Explorer実環境相関測定
9. `devenv`・`main`への統合

既存の製品不変条件、ログ削除禁止、ファイル内容・内容ハッシュ非取得、MVPドライバーなし、子孫人工イベント非生成は変更しない。

## 2. 現状基準

トップCodexは作業開始時にリポジトリの実状態を再確認する。少なくとも次を読む。

- `AGENTS.md`
- `TOP_CODEX.md`
- `Requirements/00_PRODUCT_REQUIREMENTS.md`
- `Requirements/03_TEST_AND_QUALITY_REQUIREMENTS.md`
- `Requirements/13_AGENT_WINDOWS_NTFS_COLLECTOR.md`
- `Requirements/17_AGENT_AGENT_SERVICE_IPC.md`
- `Requirements/19_AGENT_INTEGRATION_QUALITY.md`
- `Requirements/20_AGENT_INSTALLER_PACKAGING.md`
- `docs/release/main-readiness.md`
- `docs/release/performance-baseline.md`
- `docs/release/windows10-compatibility.md`
- `docs/release/critical-branch-matrix.md`

2026-08-19 の公開リポジトリ確認時点では `feat/benchmark-performance` が公開既定ブランチであり、公開 `devenv` と `main` は確認できなかった。これは作業開始時に必ず `git branch -a` と remote refs で再確認し、公開状態だけを根拠にローカルブランチが存在しないと決めつけてはならない。

## 3. 追加要件の優先関係

この最終受入れフェーズに限り、`Requirements/21_*` 以降の文書が、最終受入れの実行方法、追加ブランチ、テスト環境、安全境界について古い文書を補足する。

製品仕様そのものが競合した場合は古い仕様を勝手に上書きせず停止してユーザーへ報告する。

## 4. 実行順

次の順番を崩さない。

1. `37_PHYSICAL_READ_ONLY_ACCEPTANCE.md` の静的監査、通常権限preflight、隔離fixtureと実行時監査の準備
2. `23_CONFIRMED_RECONCILIATION_EXECUTION.md`
3. `24_NTFS_CANDIDATE_METADATA_PRIVILEGE_LOW_IO.md`
4. `25_WINDOWS_PRIVILEGED_CAPABILITY_MATRIX.md`
5. `30_AGENT_EXPLORER_REAL_CORRELATION.md`
6. `27_IDLE_RESOURCE_ACCEPTANCE.md`
7. `28_PERFORMANCE_MATRIX_WITH_MFT.md`
8. `26_WINDOWS10_22H2_ACCEPTANCE.md`
9. `29_REAL_MACHINE_INSTALLER_ACCEPTANCE.md`
10. `31_DEVENV_MAIN_INTEGRATION.md`

37 の監査と隔離境界が成立する前に、製品プロセス、管理者権限付き試験、MFT大規模試験、サービス登録試験を実機で開始してはならない。各段階は前段の監査証拠がPASSしてから進む。

## 5. 推奨作業ブランチ

このフェーズでは次のブランチを使う。トップCodexはworktreeを分ける。

- `feat/physical-readonly-acceptance`（旧 `feat/testlab-hyperv` は新規作成しない）
- `feat/reconciliation-finalization`
- `feat/windows-privileged-acceptance`
- `feat/correlation-real-environment`
- `feat/resource-performance-acceptance`
- `feat/windows10-acceptance`
- `feat/installer-real-acceptance`

共有契約や既存製品コードの変更が必要な場合は、同一ファイルを複数ブランチで並行編集しない。`reconciliation-finalization` を先に完成させて統合してから、受入れブランチを最新の統合基準へ同期する。

## 6. 人間操作が必要な境界

Codexが通常権限で自律して行う監査・修正・非特権テストと、ユーザーが隔離ルート・対象物理PCを確認しUACや別PC操作を行う境界は37の第5・6節を正本とする。VM、ISO、仮想化製品の準備を依頼しない。Codex自身を管理者化しない。

## 7. 完了条件

9項目それぞれに実測artifactが存在し、`docs/release/main-readiness.md` に未完了として残らず、全自動ゲートと指定された人間実行ゲートが成功し、最後に `devenv` と `main` の統合が完了した場合だけ最終完了とする。

診断実行、Fake Collectorだけの成功、静的監査だけの成功、`NOT_EXECUTED`、`AcceptanceEligible=false` を完了扱いしてはならない。
