# Windows 10 22H2 x64 受入れ要件

## 1. 目的

Windows 10 22H2 x64を主要機能の実対応対象として実測する。

`.NET 10`やライブラリの上流サポート境界だけを根拠に互換確認済みとしない。

## 2. 二段階

### Stage A: Hyper-V Windows 10 22H2

`SC-Test-W10` で自動実行する。

- application起動
- Avalonia UI起動
- Agent起動
- Session Agent起動
- USN
- MFT
- ETW
- ReadDirectoryChangesW
- Clipboard
- SMB share
- Cloud Files API capability detection
- reconciliation
- privileged capability matrixのWindows 10適用部分
- installer clean install/repair/uninstall
- history retention
- no-driver確認

VM stageはOS互換性の強い証拠だが、「物理Windows 10実機」の完了証拠とは区別する。

### Stage B: 物理Windows 10 22H2

最終受入れ用bundleを作る。

`artifacts/manual/windows10-physical-acceptance/` に、self-contained成果物、installer、検証PowerShell、README、結果回収スクリプトを出す。

ユーザーはWindows 10 22H2 x64物理テストPCで管理者PowerShellから1コマンドだけ実行する。

Codexはリモートでその物理PCを勝手に操作しない。

## 3. 物理機で破壊的試験をしない

Windows 10物理機では、システムC:をMFT破壊・format・journal改変対象にしない。

特権ファイルシステム試験は物理機上でも専用VHDXを作成してその中だけで行う。

通常monitoring、Service、Session Agent、UI、Explorer相関は物理OS上で確認してよい。

## 4. 実行前検査

スクリプトは次をfail-closed検査する。

- `ProductName`
- `DisplayVersion == 22H2`
- x64
- OS buildを記録
- 管理者権限
- 十分な空き容量
- TestLab VHDX作成先

条件不一致では互換テストを実行せず結果をFAIL/WRONG_ENVIRONMENTとする。

## 5. Windows 10で不可能な機能

Windows 11だけに存在するAPIや挙動が見つかった場合、勝手にWindows 11限定へ変更しない。

結果artifactへ次を記録して停止する。

- API名
- Windows 10でのerror code
- 製品機能への影響
- Windows 10用代替実装案
- 代替実装のコスト

ユーザー判断後にのみ要件を変更する。

## 6. 完了条件

Stage AがPASSし、Stage Bの実物理Windows 10 22H2 artifactがPASSしたときだけ `docs/release/windows10-compatibility.md` を「measured compatible」相当に更新する。

物理機が用意できない間はこの項目だけ未完了として正直に残す。
