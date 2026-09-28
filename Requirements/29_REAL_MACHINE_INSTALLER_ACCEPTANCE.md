# 実機インストーラー受入れ要件

## 1. 目的

WiX MSIをWindows物理テストPCで受け入れ、サービス登録、Session Agent、UI、更新、repair、rollback、uninstall、history retentionを確認する。既存製品や履歴のあるPCでこのmatrixを実行しない。

## 2. 実行順

1. `37_PHYSICAL_READ_ONLY_ACCEPTANCE.md` の静的監査と実機preflight
2. 専用Windows 11物理テストPCで完全installer matrix
3. 専用Windows 10 22H2物理テストPCで `26_WINDOWS10_22H2_ACCEPTANCE.md` と合わせてinstaller acceptance

既存インストール、既存履歴、サービス名・製品登録の衝突がある場合は停止し、別の専用物理テストPCを使う。仮想環境での事前合格は要求しない。

## 3. 物理機での人間操作

Codexはユーザーの物理Windowsへ無断でMSIをインストールしない。

Codexは `artifacts/manual/installer-acceptance/` に次を生成する。

- MSI
- hash manifest
- `Run-RealMachineInstallerAcceptance.ps1`
- `Cleanup-RealMachineInstallerAcceptance.ps1`
- 実行内容README

ユーザーが監査済みbundleのhash、対象、操作内容を確認し、UACが必要なMSI・サービス操作を承認する。Codexアプリを管理者として起動しない。

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

既存のStorage Chronicleインストールまたは履歴があるPCではinstaller matrixを実行しない。別の専用物理テストPCを要求する。テスト専用rootにも既存ファイルがあれば上書きせず停止する。

アンインストール試験で既存ユーザーデータを削除しない。

## 7. 完了条件

Windows 11物理機でmatrixがPASSし、Windows 10物理機は26の受入れと合わせてPASSする。

物理機テストが未実行ならinstallerは未受入れと明示し、過去のVM結果を現在の実機受入れへ流用しない。
