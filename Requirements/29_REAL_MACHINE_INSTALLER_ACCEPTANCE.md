# 実機インストーラー受入れ要件

## 1. 目的

WiX MSIをVMだけでなくWindows物理機でも受け入れ、サービス登録、Session Agent、UI、更新、repair、rollback、uninstall、history retentionを確認する。

## 2. 実行順

1. Windows 11 Hyper-V VMで完全installer matrix
2. Windows 10 Hyper-V VMでinstaller matrix
3. Windows 11物理機で最終installer acceptance
4. Windows 10物理機では `26_WINDOWS10_22H2_ACCEPTANCE.md` と合わせてinstaller acceptance

VMでFAILする状態では物理機へ進まない。

## 3. 物理機での人間操作

Codexはユーザーの物理Windowsへ無断でMSIをインストールしない。

Codexは `artifacts/manual/installer-acceptance/` に次を生成する。

- MSI
- hash manifest
- `Run-RealMachineInstallerAcceptance.ps1`
- `Cleanup-RealMachineInstallerAcceptance.ps1`
- 実行内容README

ユーザーが管理者PowerShellでスクリプトを起動し、UACを承認する。

スクリプトは実行前に製品名、MSI hash、install target、data targetを表示し、ユーザーがyesを入力してから変更する。

## 4. 既定位置

受入れでは製品の既定位置を検証する。

- binaries: `%ProgramFiles%\Storage Chronicle\`
- data: `%ProgramData%\Storage Chronicle\`

受入れスクリプトやテスト用ファイルを既存ユーザーデータディレクトリへ混在させない。

## 5. 必須matrix

- clean install
- single product entry
- Agent LocalSystem automatic service
- recovery 5s/15s/60s
- Session Agent logon start
- UI normal-user launch
- history/data directory作成とACL
- repair
- same-major update path
- Agent安全停止→置換→再起動
- intentionally failed install/updateのrollback
- half-registered serviceなし
- uninstall
- uninstall後history retention
- uninstallにhistory deletion optionが存在しない
- driverを導入しない
- self-containedで別.NETランタイム手動導入不要
- reinstall後に保持historyを再利用可能

## 6. データ安全

実機受入れではhistory retention確認用のテストデータだけを使用する。

既存ユーザーログがあるPCでは、受入れ前に別のtest data rootを設定できないなら実行せず、ユーザーへ専用テストPCを要求する。

アンインストール試験で既存ユーザーデータを削除しない。

## 7. 完了条件

Windows 11物理機でmatrixがPASSし、Windows 10物理機は26の受入れと合わせてPASSする。

物理機テストが未実行ならinstallerはVM verifiedとだけ表現し、real-machine acceptedと書かない。
