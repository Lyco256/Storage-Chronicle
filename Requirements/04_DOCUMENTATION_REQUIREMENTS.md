# 文書要件

## 1. 文書構成

```text
docs/
├─ architecture/
├─ decisions/
├─ handoffs/
├─ release/
├─ src/
├─ build/
├─ tools/
├─ installer/
└─ benchmarks/
```

## 2. ソースミラー

`docs/src`は`src`、`docs/build`は`build`、`docs/tools`は`tools`、`docs/installer`は`installer`、`docs/benchmarks`は`benchmarks`のディレクトリ構造をそのまま再現する。

例：

```text
src/StorageChronicle.State/Paths/PathResolver.cs
docs/src/StorageChronicle.State/Paths/PathResolver.cs.md

src/StorageChronicle.UI.DiffView/Views/DiffTreeView.axaml
docs/src/StorageChronicle.UI.DiffView/Views/DiffTreeView.axaml.md
```

元の拡張子を残して`.md`を追加する。これにより`.axaml`と`.axaml.cs`を区別する。

## 3. 文書が必要なファイル

- 手書き`.cs`。
- `.axaml`。
- PowerShell、Shell、SQL、その他のアプリ固有ソース。
- 生成コード、`obj`、`bin`、自動生成AssemblyInfoは対象外。
- 単純なリソースファイルは、所属ディレクトリREADMEで説明してよい。

## 4. 各ファイル文書の内容

同じ意味の説明を繰り返さず、最低限次を書く。

- 役割。
- 含まれるクラス、レコード、インターフェース、Viewの責務。
- 入力と出力。
- 依存先と依存理由。
- 重要な不変条件。
- スレッド、非同期、ライフタイム。
- 例外・失敗時の扱い。
- 対応するテスト。
- OS固有制約。
- 変更時に壊れやすい契約。

コードを一行ずつ言い換える説明は禁止する。

## 5. ディレクトリREADME

各`src/<Project>`と主要サブディレクトリに対応する`docs/src/<Project>/README.md`を作る。

- ディレクトリの責務。
- 所有する概念。
- 外部へ公開する契約。
- 内部で閉じる実装。
- 他プロジェクトとの依存方向。

## 6. Architecture Decision Record

重大判断は`docs/decisions/ADR-XXXX-<slug>.md`へ記録する。最低限：

- Avalonia採用。
- MVVMをUIだけへ限定。
- 追記ログと再構築可能SQLite。
- MVPでドライバーなし。
- 公開APIによるNTFS列挙。
- フォルダー移動で子孫イベントを生成しない。
- 外付け媒体の不変セグメントと分岐。
- ログ削除禁止。

ADRは状況、決定、理由、結果、却下案を含める。

## 7. ハンドオフ

サブエージェントは`docs/handoffs/<branch-slug>.md`を作る。

- 担当要件。
- 所有パス。
- 変更概要。
- 重要な設計判断。
- 実行テストと結果。
- 性能測定。
- 既知の制限。
- 共有契約変更要求。
- トップCodexが確認すべき点。

## 8. 自動検査

`tools/StorageChronicle.DocMirrorValidator`を作る。

- 対象ソースに対応文書があるか。
- 存在しないソースの孤立文書がないか。
- 各文書に必須見出しがあるか。
- 文書内の対応テストパスが存在するか。
- 失敗時に非ゼロ終了する。

全PR相当のマージ前ゲートにする。サブエージェントはソースと同じブランチで文書を更新する。
