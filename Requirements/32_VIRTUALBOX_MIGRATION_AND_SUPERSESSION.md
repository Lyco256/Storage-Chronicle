# VirtualBox TestLab移行・Hyper-V要件上書き

## 1. 目的

Storage ChronicleのWindows特権・実I/O・MFT・USN・ETW・Service・Installer・相関受入れ環境を、Windows 11 Homeホストで利用できるOracle VirtualBoxへ移行する。

この文書と `33_SAFE_VIRTUALBOX_TESTLAB.md` は、TestLabの仮想化基盤に限って既存のHyper-V前提要件を上書きする。製品仕様、Windows Collector、受入れ項目、VHDXを使ったゲスト内部の破壊可能領域、ログ品質要件は変更しない。

`Requirements/22_SAFE_HYPERV_TESTLAB.md` が存在する場合、そのHyper-V固有部分は以後正本ではない。移行完了後、同ファイルは削除せず、先頭に「TestLab基盤は32/33によりVirtualBoxへ置換済み」と明記して誤実行を防ぐ。

## 2. 作業開始前の実装監査

トップCodexは最初に `git fetch --all --prune` を実行し、ローカル・remoteの全branchとworktreeを確認する。公開GitHubの表示だけを完全な実装状態とみなさない。

次の語をリポジトリ全体で検索し、`artifacts`、`.git`、生成物を除外した一覧を `docs/release/virtualbox-migration-inventory.md` に保存する。

- `Hyper-V`
- `Microsoft-Hyper-V`
- `New-VM`
- `Set-VM`
- `Start-VM`
- `Stop-VM`
- `Checkpoint-VM`
- `Restore-VMSnapshot`
- `New-PSSession -VMName`
- `New-VHD`
- `Mount-VHD`
- `Dismount-VHD`
- `vmms`
- `Hyper-V Administrators`
- `PowerShell Direct`
- `TestLab`

各該当箇所を必ず `KEEP`, `REWRITE`, `REMOVE`, `HISTORICAL_DOC` の4分類にする。分類を行う前に削除を始めない。

## 3. 公開実装から確認済みのKEEP対象

次は仮想化製品に依存しないため、原則として流用する。

- `build/Test-WindowsPrivileged.ps1`
  - `AcceptanceRoot`
  - `TestLabRoot`
  - `VhdxPath`
  - `VhdxRoot`
  - `DevicePath`
  - `RemovableRoot`
  - `SmbShareName`
  - `ServiceName`
  - `SessionAgentExecutable`
  - `WorkloadOraclePath`
  - `AgentHistoryPath`
  - fail-closedな受入れ入口
- `build/Test-Privileged.ps1` の次の部分
  - TestLabRoot境界チェック
  - `.vhdx`拡張子チェック
  - 既存VHDX上書き拒否
  - `SC_TEST_VOLUME` marker
  - `StorageChronicleTestVolume.json`
  - `Assert-TestLabMarker`
  - volume root・protected path拒否
  - `Get-DiskImage`によるattach状態確認
  - 既存USN journalだけを読む規則
  - USN/MFT/ETW/ReadDirectoryChangesW/SMB/Service/Session/Clipboard/VolumeGuid/ACL/NonNTFSの受入れ
  - acceptance manifest
  - `NOT_EXECUTED`を成功扱いしない規則
- `build/quality/AcceptanceContracts.ps1`
- `build/quality/Test-FullBenchmarkMatrix.ps1`
- `build/quality/Test-ResourceBudgetAcceptance.ps1`
- `build/quality/New-ResourceQuietWitness.ps1`
- `build/quality/Test-CorrelationMetrics.ps1`
- `tools/StorageChronicle.ResourceMonitor`
- `tools/StorageChronicle.TestDataGenerator`
- 既存のWindows integration test projects
- `docs/release/main-readiness.md` のblocking acceptance管理
- ゲストWindows内部で行うUSN/MFT/ETW/Service/SMB等の実テスト

KEEP対象をVirtualBox移行だけを理由に作り直さない。

## 4. 必須REWRITE対象

### 4.1 VM制御

Hyper-V VM操作は次へ一対一で置換する。

| Hyper-V側概念 | VirtualBox側 |
|---|---|
| `New-VM` | `VBoxManage createvm` |
| VM CPU/RAM設定 | `VBoxManage modifyvm` |
| Generation 2 / UEFI | `VBoxManage modifyvm --firmware efi` |
| 仮想TPM | `VBoxManage modifyvm` のTPM 2.0設定 |
| `Start-VM` | `VBoxManage startvm` |
| `Stop-VM` | guest shutdown、失敗時だけ `VBoxManage controlvm ... poweroff` |
| checkpoint | `VBoxManage snapshot ... take` |
| baseline restore | `VBoxManage snapshot ... restore` |
| PowerShell Direct | Guest Additions + `VBoxManage guestcontrol` |
| Copy-VMFile等 | `VBoxManage guestcontrol ... copyto/copyfrom` |
| Hyper-V host info | `VBoxManage list hostinfo` |

VM操作を製品コードへ入れない。`tools/TestEnvironment` と `build` のTestLab orchestrationだけに閉じ込める。

### 4.2 VHDX生成

公開実装の `build/Test-Privileged.ps1` には次のHyper-V module依存があるため置換する。

- `New-VHD`
- `Mount-VHD`
- `Dismount-VHD`

`-CreateVhdx`、`-VhdxPath`、`-VhdxRoot` の外部契約は維持する。

新実装はWindows標準機能だけを使用する。

1. `diskpart.exe` の `create vdisk file="<path>.vhdx" maximum=<MiB> type=expandable`
2. `select vdisk file="<path>.vhdx"`
3. `attach vdisk`
4. `Get-DiskImage -ImagePath <path> | Get-Disk`
5. `Initialize-Disk`
6. `New-Partition`
7. `Format-Volume`
8. cleanupは `Dismount-DiskImage -ImagePath <path>` またはDiskPartの `detach vdisk`
9. attach再試験は `Mount-DiskImage -ImagePath <path>`

`Get-DiskImage`を使った既存attached判定はそのまま利用する。

DiskPartへ渡すscriptはTestLabRoot配下の一時ファイルに生成し、絶対パスを検証してから実行する。VHDX以外のdisk番号をフォーマットする設計へ変更してはならない。

### 4.3 Preflight

次のHyper-V固有判定を削除してVirtualBox判定へ置換する。

- Windows Pro/Enterprise必須
- Hyper-V feature必須
- Hyper-V PowerShell module必須
- `vmms`必須
- SLATをHyper-V導入条件としてブロック
- PowerShell Direct availability

代わりに34のVirtualBox preflightを実装する。

## 5. REMOVE対象

VirtualBox経路が同等以上に動作し、全callerを更新した後にだけ次を削除する。

- Hyper-V featureを有効化するrepo内スクリプト
- `New-VM`等だけを呼ぶHyper-V専用TestLab wrapper
- `vmms`だけを検査するコード
- PowerShell Direct専用copy/execute helper
- Hyper-V checkpoint専用helper
- Hyper-V専用VM名・Generation・switchを前提にした設定
- `New-VHD/Mount-VHD/Dismount-VHD`だけの互換helper

汎用名の `Initialize-TestLab.ps1` 等は削除せず、中身をVirtualBox実装へ置換する。

過去のacceptance artifact、Git履歴、設計経緯を示すhistorical documentationは削除しない。

## 6. やってはいけない置換

- Windows特権テストをFake Eventだけへ落とさない。
- VHDX安全境界をなくしてVMのC:を破壊試験対象にしない。
- `NOT_EXECUTED`をVirtualBox移行成功とみなさない。
- 製品のUSN/MFT/ETWコードをVirtualBox向けに分岐させない。
- VirtualBox用の特別な製品挙動を追加しない。
- ホスト物理ディスクのraw accessを利用しない。
- VirtualBox shared folderを破壊試験対象にしない。

## 7. branch

移行は `feat/testlab-virtualbox-migration` で実施する。

既存の製品ロジックを同時に大規模変更しない。confirmed reconciliation等の製品機能branchが未統合なら、VirtualBox移行はTestLab・runner互換部分だけを先に完成させる。

## 8. 完了条件

- Hyper-VをインストールしていないWindows 11 HomeホストからTestLabを制御できる。
- Windows 11 guestで既存Windows特権matrixを実行できる。
- `-CreateVhdx` がHyper-V moduleなしで実行できる。
- Hyper-V専用実行コードがproduction/TestLab runtime pathに残らない。
- KEEP対象の受入れロジックを不要に再実装していない。
- 既存の安全markerとfail-closed規則が維持される。
