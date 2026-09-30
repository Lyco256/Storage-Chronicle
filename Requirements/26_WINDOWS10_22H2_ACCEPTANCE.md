# Windows 10 22H2 x64 受入れ要件

## 1. 目的

Windows 10 22H2 x64を主要機能の実対応対象として実測する。

`.NET 10`やライブラリの上流サポート境界だけを根拠に互換確認済みとしない。

## 2. 実機受入れ

仮想環境のStage Aは行わない。以下の項目をWindows 10 22H2 x64物理テストPCで実測する。既存のStage A用スクリプトやmanifestをそのままPASSの根拠にしない。

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

最終受入れ用bundleを作る。

`artifacts/manual/windows10-physical-acceptance/` に、self-contained成果物、installer、検証PowerShell、README、結果回収スクリプトを出す。

ユーザーはWindows 10 22H2 x64物理テストPCで監査済みbundleと対象パスを確認する。UACが必要な部分だけユーザーが承認する。Codexアプリ自体は管理者として起動しない。

Codexはリモートでその物理PCを勝手に操作しない。

## 3. 物理機で破壊的試験をしない

Windows 10物理機では、システムC:をMFT破壊・format・journal改変対象にしない。

特権ファイルシステム試験は37の静的監査と実機preflightを通過後、承認済み専用ルート内でこのrunに新規作成したfile-backed VHDXだけで行う。

通常monitoring、Service、Session Agent、UI、Explorer相関は物理OS上で確認してよい。

## 4. 実行前検査

スクリプトは次をfail-closed検査する。

- `ProductName`
- `DisplayVersion == 22H2`
- x64
- OS buildを記録
- 特権capabilityに必要な権限。Codex本体は通常権限のままとし、UACが必要なら37の人間引継ぎを使う
- 十分な空き容量
- 承認済みrun専用VHDX作成先と既存データ不存在

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

37の監査と実物理Windows 10 22H2 artifactが全てPASSしたときだけ `docs/release/windows10-compatibility.md` を「measured compatible」相当に更新する。

物理機が用意できない間はこの項目だけ未完了として正直に残す。
