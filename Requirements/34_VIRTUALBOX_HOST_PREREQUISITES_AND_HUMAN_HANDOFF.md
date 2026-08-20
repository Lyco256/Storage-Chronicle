# VirtualBoxホスト事前条件・人間引継ぎ要件

## 1. 現在確認済みホスト

ユーザーが2026-08-20に取得した値を初期基準とする。

- OS: Windows 11 Home
- CPU: Intel Core i5-1235U
- architecture: AMD64 / x64
- cores: 10
- logical processors: 12
- RAM: 15.8 GiB
- storage: Samsung NVMe SSD 約476.9 GiB
- C: NTFS
- C: free: 約172.8 GiB
- TPM present/ready/enabled/activated: true
- Task Manager上のvirtualization: enabled
- `Win32_Processor.VirtualizationFirmwareEnabled`: false

Intel公式仕様上、i5-1235UはVT-x、VT-d、VT-x with EPTをサポートする。EPTはSLAT相当である。

WMIの `VirtualizationFirmwareEnabled=false` 単独を停止条件にしない。Task Managerとの矛盾があるため、VirtualBox導入後の実host capabilityと実VM起動を最終判定にする。

## 2. Preflight

`Test-TestLabPrerequisites.ps1` は通常ユーザーで実行する。

必須出力:

- Windows edition/build
- x64
- CPU model
- core/thread count
- total/available RAM
- free disk
- NTFS
- TPM state
- WMI virtualization value
- VirtualBox install path
- `VBoxManage --version`
- `VBoxManage list hostinfo`
- VirtualBox host capability
- Hyper-V/VBS/Memory Integrityの検出結果
- configured ISO paths
- TestLabRoot
- current VM list
- current snapshot list
- ReadyForProvisioning
- ReadyForFunctionalAcceptance
- ResourceProfile

`VirtualizationFirmwareEnabled=false`でも、VirtualBoxがhardware virtualizationを使用してWindows x64 guestを正常起動できればfunctional preflightをPASSしてよい。

## 3. VirtualBox導入

CodexはOracle公式サイト以外のinstallerを使用しない。

基準はVirtualBox 7.2.16 Windows Hosts。

Codexが行ってよいこと:

1. Oracle公式download URLとversionを確認する。
2. installerをユーザー承認済み一時ディレクトリへdownloadする。
3. Authenticode署名、publisher、file versionを検査する。
4. 検査結果とinstaller pathを表示する。

Codexが行ってはいけないこと:

- 自分で昇格してinstallerを実行
- UAC回避
- unsigned/repacked installer実行
- Extension Pack自動導入

VirtualBox未導入ならここで作業を中断し、人間引継ぎを出す。

## 4. 人間引継ぎの固定フォーマット

権限、host設定、firmware、Windows installer、ISO、guest対話設定で自動進行できない場合、Codexは勝手な回避策へ進まずその場で中断する。

報告には必ず次を含める。

### Blocked
何の作業で停止したか。

### Reason
検出した実エラー、exit code、現在値。

### Why user action is required
なぜ通常権限のCodexから安全に処理できないか。

### Do this
ユーザーが行う手順を番号付きで具体的に記載する。UI操作なら画面名、CLIなら完全なコマンドを示す。

### Expected result
成功した場合にユーザーが確認できる表示・値・ファイルを示す。

### Do not do
危険な代替操作や不要な権限付与を明記する。

### Resume command
ユーザー操作後に実行すべきPreflightまたは再開コマンドを1つ示す。

### Send back
ユーザーがCodexへ返す情報を限定する。パスワード、product key、秘密情報を要求しない。

この報告を出したら、ユーザーが完了を返すまで後続の破壊的処理へ進まない。

## 5. 想定handoff

### A. VirtualBox installation

ユーザーへ署名確認済みinstaller pathを示す。

手順:

1. Codexアプリ/CLIを管理者化しない。
2. Explorerからinstallerを起動する。
3. Oracle VirtualBoxのUACだけ承認する。
4. 標準VirtualBox本体を導入する。
5. Extension Packは導入しない。
6. 再起動を求められた場合は保存作業後にユーザーが再起動する。
7. 通常権限のPowerShellで `VBoxManage --version` を確認する。

### B. Hardware virtualization error

VirtualBox実VMがVT-x/EPTを使用できず起動失敗した場合だけfirmware確認を求める。

1. Windowsを再起動してUEFI/BIOSへ入る。
2. Intel Virtualization Technology / VT-xをEnabledにする。
3. VT-dは利用可能ならEnabledにする。
4. 保存してWindowsへ戻る。
5. Task Manager > Performance > CPUでVirtualization: Enabled確認。
6. Preflight再実行。

CPU自体はVT-x/EPT対応済みなので、CPU交換を要求しない。

### C. VBS / Memory Integrity

VirtualBoxが起動できない、またはVirtualBox自身がHyper-V/VBS backendによる問題を明示した場合だけ報告する。

CodexはMemory Integrity、Credential Guard、VBS、Windows Hypervisor Platformを自動で無効化しない。

functional testが正常に動くなら、性能低下だけを理由にsecurity機能を無効化しない。

本当に無効化が必要な場合は、セキュリティ低下を説明し、ユーザーの明示判断を待つ。

### D. Windows ISO

公式ISOがない場合は、Microsoft公式Windows downloadページから対象ISOを取得するよう案内する。

非公式mirror、改変ISO、activation bypassを提案しない。

### E. Guest Windows setup

unattended installがlicense/EULA/OOBEで停止した場合だけ、VM画面でその部分をユーザーに一回完了してもらう。

host管理者資格情報をguestへ入力させない。

## 6. ディスク・メモリ停止条件

次は自動的に安全停止する。

- host available RAM < 6 GiB で新規VMを起動しようとした
- TestLabRootを含むvolume free < 40 GiB
- 1M MFT seed作成前にfree < 60 GiB
- Windows 10/11 VMを同時起動しようとした
- TestLabRootがOneDrive、repo、ProgramData\Storage Chronicle、ユーザードキュメント配下
- raw physical disk accessが設定されている
- write-enabled shared folderが存在
- guestのTestLab VHDX markerが一致しない

resource不足ではguest RAMやテスト件数を勝手に減らしてPASSさせず、中断してユーザーへ閉じるアプリや空き容量確保を依頼する。

## 7. 完了条件

Preflightが単一WMI値ではなく実VirtualBox能力を判定し、ユーザー操作が必要なときだけ固定handoffで停止する。

通常の移行・テスト中に「管理者PowerShellでCodexを起動してください」と案内してはならない。
