# 安全なVirtualBox TestLab要件

## 1. 固定基盤

ホストはWindows 11 Home x64を許可する。

仮想化製品は Oracle VirtualBox 7.2.16 Windows Hosts版を基準バージョンとする。実装時に7.2.16がOracle公式配布から取得不能になっている場合は、同じ7.2系の後続安定版だけを候補にし、勝手にmajor/minorを変更せずユーザーへ報告する。

Extension Packは導入しない。今回の必須テストにUSB passthrough、RDP、NVMe emulation等のExtension Pack依存機能を使用しない。

VirtualBox Guest Additionsはhost VirtualBoxと同じ系列をguestへ導入する。`VBoxManage guestcontrol`による成果物転送とコマンド実行に使用する。

## 2. ホスト上の安全境界

Codex自体はホストで管理者として起動しない。

VirtualBox本体の初回インストールやhost driver変更でUACが必要になった場合だけ34のhuman handoffを使う。

禁止:

- CodexからUAC回避を試みる
- raw physical disk mapping
- hostのC:をguestのraw diskとして渡す
- shared folderをread-writeで公開する
- shared clipboard有効化
- drag-and-drop有効化
- host USB deviceのpassthrough
- host network shareへのguest資格情報保存
- hostのStorage Chronicleログ領域をguestへ直接見せる

TestLabデータはユーザー承認済みの `SC_TESTLAB_ROOT` 配下だけに置く。

## 3. Windows 11 guest

VM名は `SC-Test-W11-VBox`。

固定構成:

- x86_64
- 2 vCPU
- RAM 4096 MiB
- EFI有効
- TPM 2.0 emulation
- OS disk: dynamically allocated VDI、logical 80 GiB
- video memoryはUI受入れに必要な範囲だけ
- 3D acceleration無効
- audio無効
- webcam無効
- shared foldersなし
- shared clipboard無効
- drag-and-drop無効
- USB controllerは必須ではないので無効
- provisioning時だけNATを許可
- baseline完成後はnetwork adapterを切断

Windows 11 guestのEditionは、製品互換性を検証する目的でHomeを第一候補とする。テスト機能にPro/Enterpriseが本当に必要なことが実測で判明した場合は勝手にguest editionを変更せず報告する。

## 4. Windows 10 guest

VM名は `SC-Test-W10-VBox`。

固定構成:

- Windows 10 22H2 x64
- 2 vCPU
- RAM 4096 MiB
- OS disk: dynamically allocated VDI、logical 64 GiB
- 3D/audio/shared folder/shared clipboard/drag-and-drop/USBは無効
- provisioning時だけNAT
- baseline完成後はnetwork adapter切断

Windows 10 VMとWindows 11 VMを同時起動しない。

## 5. guest管理者

guest内にTestLab専用ローカル管理者アカウントを一つ作る。

資格情報はrepoへ保存しない。Git管理外の `TestLab.local.psd1` かWindows Credential Manager等のlocal-only領域へ保存する。

この資格情報は `VBoxManage guestcontrol` のguest内操作だけに使う。

ホストの管理者資格情報をguestへ再利用しない。

## 6. provisioning

第一選択はVirtualBoxのunattended installationとする。

Windowsのライセンス/EULA/初回設定を完全に安全自動化できない場合は、無理に回避せず34に従って一回だけユーザーへguestセットアップを依頼する。

activation bypass、非公式ISO、改変ISOを使用しない。

Guest Additions導入後に、次をbaseline smokeとして一度だけ確認する。

- guestcontrolで `whoami`
- guestcontrolでPowerShell起動
- hostから小さいtest artifactをcopyto
- guestからresult JSONをcopyfrom
- guest内で管理者権限確認
- network切断後もguestcontrolが動作

## 7. snapshot

各OSに `SC-CLEAN-BASELINE` を1つだけ維持する。

通常acceptance runごとに新しい長期snapshotを積まない。

実行フロー:

1. VM電源OFF
2. `SC-CLEAN-BASELINE` restore
3. VM起動
4. guest ready確認
5. test artifact転送
6. guest内でテスト
7. result回収
8. guest正常shutdown
9. baseline restore

テスト中にguestが壊れた場合はresult回収できる範囲だけ回収し、VMを修復せずbaselineへ戻す。

## 8. guest内部の破壊可能領域

OS disk C:を大量mutation、format、journal操作の対象にしない。

実ファイルシステム破壊試験は `build/Test-Privileged.ps1 -CreateVhdx` がguest内に作る使い捨てVHDXへ限定する。

VHDXはWindows標準DiskPart/Storage機能で作成・attachする。Hyper-V moduleを要求しない。

VHDX markerは既存の次を維持する。

- `SC_TEST_VOLUME`
- `.storage-chronicle-testlab-marker.json`
- `StorageChronicleTestVolume.json`
- TestRun GUID
- TestLabRoot絶対パス検証

## 9. 実I/O workload

Windows Collector受入れはFake Eventで代用しない。

既存 `StorageChronicle.TestDataGenerator` が実mutation workloadとして必要な操作・oracleを満たせる場合は拡張して使う。満たせない場合だけ `tools/StorageChronicle.FileMutationWorkload` を追加する。

最低操作:

- create
- empty create
- write
- truncate/extend
- metadata/attribute change
- rename
- same-volume move
- directory move
- delete
- directory delete
- short-lived object
- same-name/new-File-ID replacement
- multiple-process mutation
- burst
- large zero-byte namespace dataset

oracleにはscenario ID、PID、process start time、exe path、relative path、expected high-level change、start/end timeを保存する。

## 10. TestLab操作

最低限次の汎用スクリプトを用意する。既に同名Hyper-V版が存在する場合は名前を増やさず中身を置換する。

- `Test-TestLabPrerequisites.ps1`
- `Initialize-TestLab.ps1`
- `Reset-TestVm.ps1`
- `Invoke-TestLabCommand.ps1`
- `Copy-TestArtifactsToVm.ps1`
- `Copy-TestResultsFromVm.ps1`
- `Invoke-WindowsTestLab.ps1`

VirtualBox固有処理を共通helperへまとめ、各acceptance scriptへ`VBoxManage`呼出しを散在させない。

## 11. 完了条件

- hostがWindows 11 Homeでも構築可能
- host Codex非管理者のままVM日常操作可能
- guest内特権テストだけguest管理者で実行
- hostからguestへwrite可能共有フォルダーなし
- baseline restore可能
- guest内VHDX以外の破壊操作をfail-closedで拒否
- Windows 11/Windows 10を一台ずつ起動して全該当acceptanceへ接続可能
