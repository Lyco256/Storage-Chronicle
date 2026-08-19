# 最終受入れフェーズ統括要件

## 1. 目的

この文書は、既存の `Requirements/00_PRODUCT_REQUIREMENTS.md` ～ `Requirements/20_AGENT_INSTALLER_PACKAGING.md` を変更せず、2026-08-19 時点で未完了の最終受入れ項目を閉じるための追加要件である。

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

1. `22_SAFE_HYPERV_TESTLAB.md` のPreflightとTestLab構築
2. `23_CONFIRMED_RECONCILIATION_EXECUTION.md`
3. `24_NTFS_CANDIDATE_METADATA_PRIVILEGE_LOW_IO.md`
4. `25_WINDOWS_PRIVILEGED_CAPABILITY_MATRIX.md`
5. `30_AGENT_EXPLORER_REAL_CORRELATION.md`
6. `27_IDLE_RESOURCE_ACCEPTANCE.md`
7. `28_PERFORMANCE_MATRIX_WITH_MFT.md`
8. `26_WINDOWS10_22H2_ACCEPTANCE.md`
9. `29_REAL_MACHINE_INSTALLER_ACCEPTANCE.md`
10. `31_DEVENV_MAIN_INTEGRATION.md`

22 の安全境界が成立する前に、管理者権限付き破壊的ファイルシステム試験、MFT大規模試験、サービス登録試験をホスト上で開始してはならない。

## 5. 推奨作業ブランチ

このフェーズでは次のブランチを使う。トップCodexはworktreeを分ける。

- `feat/testlab-hyperv`
- `feat/reconciliation-finalization`
- `feat/windows-privileged-acceptance`
- `feat/correlation-real-environment`
- `feat/resource-performance-acceptance`
- `feat/windows10-acceptance`
- `feat/installer-real-acceptance`

共有契約や既存製品コードの変更が必要な場合は、同一ファイルを複数ブランチで並行編集しない。`reconciliation-finalization` を先に完成させて統合してから、受入れブランチを最新の統合基準へ同期する。

## 6. 人間操作が必要な境界

Codexに自動実行させない操作は次だけである。

- Hyper-Vが無効の場合のWindows機能有効化とホスト再起動
- TestLab保存先とWindows ISOパスの初回承認
- Windowsライセンス条項への同意が対話的に必要な場合
- Windows 10 22H2物理機での最終受入れスクリプト起動とUAC承認
- 実機インストーラー最終受入れの起動とUAC承認
- GitHub既定ブランチ変更がCLI認証で実行できない場合のWeb UI操作

それ以外のVM作成、VHDX作成、VM復元、成果物転送、テスト、ログ回収、VM破棄はCodexがスクリプト化して実行する。

## 7. 完了条件

9項目それぞれに実測artifactが存在し、`docs/release/main-readiness.md` に未完了として残らず、全自動ゲートと指定された人間実行ゲートが成功し、最後に `devenv` と `main` の統合が完了した場合だけ最終完了とする。

診断実行、Fake Collectorだけの成功、VMで代替した物理機必須ゲート、`NOT_EXECUTED`、`AcceptanceEligible=false` を完了扱いしてはならない。
