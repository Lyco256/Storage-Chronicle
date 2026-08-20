# 安全なHyper-V TestLab要件

> **Superseded for TestLab virtualization:** Requirements 32 and 33 replace the Hyper-V-specific host, VM-control, guest-control, and VHDX orchestration portions of this document with the VirtualBox TestLab contract. This file is retained as historical requirement context; product behavior, Windows Collector behavior, safety markers, acceptance evidence, and guest-internal VHDX rules remain in force unless Requirements 32–36 explicitly supersede them.

## 1. 目的

Windows特権API、USN、MFT、Service、ETW、SMB、外付け媒体相当、再調整、性能試験をホストの実データから隔離する。

Hyper-V VMとVM内の使い捨てVHDXだけを破壊可能領域とする。通常のUnit、Contract、Avalonia Headlessテストはホストで実行し、特権または実ファイルシステム状態が必要な試験だけTestLabで実行する。

## 2. ホストPreflight

トップCodexは最初に読み取り専用の `tools/TestEnvironment/Test-TestLabPrerequisites.ps1` を実装して実行する。

最低限検出する。

- Windows edition
- Hyper-V利用可能性
- Hyper-V feature状態
- BIOS/UEFI仮想化要件
- Hyper-V PowerShell module
- 管理者権限の有無
- 使用可能メモリ
- 候補保存ボリュームの空き容量
- Windows 11 ISOパス
- Windows 10 22H2 ISOパスの有無

Hyper-Vのホスト要件はMicrosoft公式仕様を基準にする。WindowsクライアントではWindows 10/11 Professional または Enterprise と、SLAT、仮想化支援、十分なメモリが必要である。

## 3. Codexが勝手に行ってはならないこと

- `Enable-WindowsOptionalFeature` 等でHyper-Vを自動有効化しない。
- BCD、Secure Boot、BIOS/UEFI設定を変更しない。
- ホストを自動再起動しない。
- Windows ISOを非公式URLからダウンロードしない。
- ライセンス条項をユーザーの代わりに同意しない。
- ホストの物理ディスクをオフライン化、初期化、フォーマットしない。
- ホストC:やソースリポジトリを破壊的テスト対象にしない。
- VMへホストのソースツリーを読み書き可能共有しない。

Hyper-Vが無効なら停止し、ユーザーへ有効化コマンドと再起動が必要であることを表示する。

## 4. ユーザーが一度だけ決める値

Preflight後、Codexは次の値を提案してユーザー承認を得る。

- `SC_TESTLAB_ROOT`: TestLab専用ディレクトリ
- `SC_WIN11_ISO`: Windows 11 x64 ISO
- `SC_WIN10_ISO`: Windows 10 22H2 x64 ISO。Windows 10受入れ前まで空でもよい。

承認値はGit管理外の `tools/TestEnvironment/TestLab.local.psd1` に保存する。秘密情報やライセンスキーは保存しない。

`SC_TESTLAB_ROOT` はリポジトリ配下、`Program Files`、`ProgramData\Storage Chronicle`、ユーザードキュメント、実外付け監視対象媒体の直下に置かない。

## 5. VM構成

CodexはPowerShell Hyper-V moduleだけで作成・管理する。

### Windows 11

- VM名: `SC-Test-W11`
- Generation 2
- 2 vCPU
- Dynamic Memory
- Startup 4096 MiB
- Minimum 2048 MiB
- Maximum 6144 MiB
- OS VHDX: dynamic 80 GiB
- Secure Boot有効
- Windows 11で必要な場合はローカルkey protectorとvTPMを構成
- 通常試験時ネットワークアダプタ切断
- Clean baseline名: `SC-CLEAN-BASELINE`

### Windows 10

- VM名: `SC-Test-W10`
- Generation 2
- 2 vCPU
- Dynamic Memory
- Startup 4096 MiB
- Minimum 2048 MiB
- Maximum 6144 MiB
- OS VHDX: dynamic 64 GiB
- 通常試験時ネットワークアダプタ切断
- Clean baseline名: `SC-CLEAN-BASELINE`

ゲスト制御と成果物転送はPowerShell Directを第一選択とし、テストのためだけにWinRM、SSH、SMB共有を構築しない。

## 6. OSプロビジョニング

CodexはISOからの自動プロビジョニングを第一選択にする。ホスト組込みのDISM、VHD/VHDX、BCDBoot、Hyper-V機能で完結する方式を優先し、追加の仮想化製品を導入しない。

対話なしの安全な自動プロビジョニングが成立しない場合だけ停止し、ユーザーにVM内Windowsセットアップを一回完了してもらう。その後のTestLab操作は再び自動化する。

## 7. テストデータVHDX

各破壊的試験はOS VHDXではなく、毎回新規作成したデータVHDXに対して行う。

通常:
- dynamic 4 GiB
- NTFS
- Volume label `SC_TEST_VOLUME`

大規模MFT:
- dynamic 16 GiB以上
- NTFS
- Volume label `SC_TEST_MFT_VOLUME`

非NTFS:
- 同じ使い捨てVHDXをFAT32またはexFATへフォーマットして使う。

各VHDXにランダムTestRun GUIDを含む `\StorageChronicleTestVolume.json` を作成する。

## 8. 破壊操作ガード

フォーマット、削除、大量書込み、USN/MFT試験、ACL変更を行うコードは、次をすべて満たさない限り即座に拒否する。

- 対象VHDXファイルの絶対パスが `SC_TESTLAB_ROOT` 配下
- 対象がHyper-V TestLab VMに接続された仮想ディスク
- Volume labelが規定値
- marker JSONのTestRun GUIDが現在実行のGUIDと一致
- System volumeではない
- Boot volumeではない
- Pagefile volumeではない
- Crashdump volumeではない
- ホスト物理ディスクではない

ドライブ文字だけを安全判定に使わない。

## 9. 実ファイル操作ワークロード

Fake Source Eventとは別に、実際のファイルシステムを操作する `tools/StorageChronicle.FileMutationWorkload` を作る。

このツールはTestLab markerがないパスでは起動を拒否する。

最低シナリオ:

- ファイル作成
- 空ファイル作成
- ディレクトリ作成
- DataWrite
- truncate/extend
- 最終更新時刻・属性変更
- rename
- 同一volume move
- directory move
- delete
- directory delete
- 短命ファイル
- 同名別File ID入替え
- 複数階層ツリー大量作成
- 複数プロセス並列操作
- 操作間隔を0にしたburst
- ACLで通常readを拒否した候補
- 既定数をパラメータ化した大量ファイル作成

ワークロードは操作前にoracle JSONを作り、各操作のscenario ID、対象相対パス、期待する高レベル変更、開始・終了時刻を記録する。SCの実ログとoracleを後で照合する。

製品テストのためにSC内部へ「ファイルを操作したことにする」特別フラグを追加してはならない。Fakeイベントはロジックテストにだけ使い、Windows Collector受入れは実I/Oで行う。

## 10. TestLab制御スクリプト

最低限次を実装する。

- `Initialize-TestLab.ps1`
- `Test-TestLabPrerequisites.ps1`
- `Reset-TestVm.ps1`
- `New-TestDataVhdx.ps1`
- `Remove-TestDataVhdx.ps1`
- `Invoke-TestLabCommand.ps1`
- `Copy-TestArtifactsToVm.ps1`
- `Copy-TestResultsFromVm.ps1`
- `Invoke-WindowsTestLab.ps1`

`Invoke-WindowsTestLab.ps1` は baseline復元、VM起動、VHDX作成、成果物転送、テスト、結果回収、VM停止、VHDX破棄まで実行する。

## 11. 「テスト環境のテスト」は作らない

TestLab自体を別テストスイートで検証する必要はない。

代わりに各実行前のPreflight、破壊操作ガード、TestRun GUID、System/Boot判定を実行時不変条件として必須にする。TestLab smokeは1回だけ行い、専用VHDXに1ファイル作成・削除でき、ホスト側対象外パスが拒否されることを確認すればよい。

## 12. 完了条件

- ユーザー承認済みTestLab root以外を変更せずVMを構築できる。
- baselineへ自動復元できる。
- 専用VHDX以外への破壊操作がfail-closedになる。
- FileMutationWorkloadの実I/OをSCがVM内で観測できる。
- ホストソースツリーをゲストから書き換えられない。
- 結果だけを `artifacts/acceptance/testlab/` へ回収できる。

## 13. 公式参照

- https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/host-hardware-requirements
- https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/powershell
- https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/overview
- https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/supported-windows-guest-operating-systems-for-hyper-v-on-windows
