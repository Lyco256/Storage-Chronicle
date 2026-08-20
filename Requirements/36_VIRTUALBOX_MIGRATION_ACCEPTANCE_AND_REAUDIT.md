# VirtualBox移行受入れ・再監査要件

## 1. 目的

MDどおり進めた結果が「Hyper-VコードをVirtualBoxコードへ置き換えただけ」ではなく、既存Storage Chronicle受入れを無駄なく実行できる理想形になっていることを確認する。

TestLabそのものへ巨大なテストスイートを追加しない。既存acceptanceを一回通すことを主証拠とする。

## 2. migration smoke

最初に次だけを確認する。

1. `VBoxManage --version`
2. hostinfo取得
3. Windows 11 VM baseline restore
4. VM start
5. guestcontrol `whoami`
6. host→guest小ファイルcopy
7. guest→host小JSON copy
8. guest shutdown
9. baseline restore

ここから先は既存Storage Chronicle acceptanceを利用する。

## 3. VHDX互換受入れ

`build/Test-WindowsPrivileged.ps1 -CreateVhdx` をWindows 11 Home guestで実行する。

確認:

- Hyper-V PowerShell moduleがguestに存在しなくても実行できる
- `.vhdx`がTestLabRoot配下にだけ生成
- dynamic VHDX
- attach後 `Get-DiskImage` でAttached=true
- NTFS / `SC_TEST_VOLUME`
- marker一致
- protected/root path拒否
- test後detach
- created VHDX削除
- 別volumeを誤フォーマットしていない

この受入れでは `New-VHD`, `Mount-VHD`, `Dismount-VHD` が呼ばれていないことをtranscriptでも確認する。

## 4. 既存Windows privileged matrix

移行完了判定には、VirtualBox Windows 11 guestで既存matrixの必須capabilityを実行する。

- Vhdx
- UsnQuery
- UsnRead
- Mft
- Reconciliation
- Etw
- ReadDirectoryChangesW
- BufferGap
- Smb
- Service
- SessionAgent
- Clipboard
- VolumeGuid
- HotAttachDetach
- AclDeniedMetadata
- NonNtfs

既存要件で未実装の製品機能が原因のFAILはVirtualBox移行FAILと製品FAILを区別して記録する。ただし最終releaseではどちらもblockingのままにする。

## 5. Benchmark/Resource再接続

`Test-FullBenchmarkMatrix.ps1` はVirtualBox orchestrationを知らない構造を維持する。

MFT device/pathはguest内専用TestLab volumeから渡す。

portable benchmarkはhost、MFT実API部分はguestという既存責務分離を維持する。

`Test-ResourceBudgetAcceptance.ps1` は物理host正式測定のままにし、VMへ移さない。

## 6. Hyper-V残存監査

runtime scriptsで次が残っていないこと。

- `New-VM`
- `Start-VM`
- `Stop-VM`
- `Checkpoint-VM`
- `Restore-VMSnapshot`
- `New-PSSession -VMName`
- `Copy-VMFile`
- `New-VHD`
- `Mount-VHD`
- `Dismount-VHD`
- `vmms`
- `Microsoft-Hyper-V-All`

許可:

- migration historyを説明するdocs
- superseded requirementの説明
- Git履歴
- Windows APIの一般説明としての「Hyper-V」という語

残存一覧は `docs/release/virtualbox-migration-inventory.md` に最終状態として更新する。

## 7. 参照切れ監査

次を機械確認する。

- Requirements内の参照ファイルが存在
- build scriptから呼ぶTestEnvironment scriptが存在
- docs/src mirror規則を満たす
- solution/project references欠落なし
- obsolete Hyper-V helperを削除した後にcallerが残っていない
- README/TOP_CODEX/AGENTSにTestLab導線がある場合、VirtualBoxを正本として示す
- `22_SAFE_HYPERV_TESTLAB.md` が存在する場合、32/33へのsuperseded noticeがある

## 8. 安全監査

次を確認する。

- raw disk attachmentなし
- write shared folderなし
- shared clipboardなし
- drag-and-dropなし
- host Codex非管理者
- Extension Packなし
- TestLabRoot外へVM imageを作らない
- guest C:をmutation rootにしない
- marker mismatch時fail-closed
- `CreateUsnJournal`拒否を維持
- physical hostのUSN journalを変更しない

## 9. 軽量性監査

15.8 GiB hostで次を確認する。

- 同時VM数 <= 1
- guest memory 4096 MiB
- vCPU 2
- portable build/benchmarkとheavy guest matrixを並列に走らせない
- 1M seedを毎回再生成しない
- clean baselineはOSごと1つ
- transient snapshot/imageがrun後に残留しない

## 10. 最終再監査

トップCodexは実装終了後、変更前のmigration inventoryと現在のrepoを比較する。

次を `docs/release/virtualbox-migration-review.md` に記録する。

- reused files
- rewritten files
- removed files
- deliberately retained Hyper-V historical references
- tests executed
- acceptance artifacts
- unresolved product blockers
- user handoffが必要な未実行項目
- disk/memory actual usage
- branch/commit

「VirtualBoxへ変えたのでHyper-V系を全部消した」のような一括説明は禁止する。既存の安全ガードや受入れロジックが残ったことをファイル単位で確認する。

## 11. 完了条件

VirtualBox移行により新しい機能欠落を作らず、Hyper-V moduleなしのWindows Home環境から既存Windows acceptanceを実行でき、TestLab以外の製品要件を変更していないこと。

人間操作待ちが残る場合は完了とせず34のhandoff形式で停止する。
