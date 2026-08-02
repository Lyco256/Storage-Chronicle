# サブエージェント要件：Projection・Grouping

## ブランチ

`feat/projection-grouping`

## 所有パス

- `src/StorageChronicle.Projection/**`
- `tests/StorageChronicle.Projection.Tests/**`
- 対応する`docs/src/StorageChronicle.Projection/**`
- `docs/handoffs/projection-grouping.md`

## 目的

記録済み事実からEvent Stack、Activity Group、Diff View用のOS非依存Projectionを生成する。表示解釈は変更可能で、Source Eventを変更しない。

## 実装要件

### Event Stack

- Source、Normalized、Groupedの3モード。
- Groupedは操作グループ、ファイル要約、Normalized、Sourceへ展開。
- 初期順序は新しいものが上。
- 昇順・降順。
- ページング。
- 展開子をページ境界で分断しない。
- プロセス名と品質。
- 記録済み親子Process Instanceを辿る詳細Projection。
- 全数照合とUnverifiedGapを独立表示。

### Activity Group

- Process Instance。Unknownは同じSource、Volume、Mount Session、表示ルート、無操作時間内だけを一つのUnknown actorとしてまとめる。
- 表示ルート。
- 無操作タイムアウト。
- 別Process Instanceが現在の表示ルートと同一、祖先、または子孫の場所を変更した場合、先行Groupを直前イベントで閉じる。元Processが後で再開しても新Groupにする。
- 読取りイベントでは分離しない。
- 既存兄弟フォルダーは別表示ルート。
- 親直下変更があれば親へ昇格。
- 新規フォルダー配下だけなら新規フォルダーをルート。
- 表示ルート計算は製品要件15章のアンカー手順をそのまま実装し、共通祖先へ任意にまとめない。
- 操作数、ファイル数、サイズ増減、継続時間、操作内訳。
- タイムアウト値を設定で変更可能。

### Diff

- Live、Period、Point-in-Time、Replayで共通モデル。
- TreeとExplorerで同じProjection。
- 主操作優先度とサブ操作。優先順はDelete、Recycle、Restore、MoveFrom、MoveTo、Copy、Create、Rename、DataWrite/Resize/Truncate、Share、CloudState、その他MetadataChangeで固定する。
- 期間内作成後削除。
- 複数名前変更。
- 同名別File ID。
- 移動後編集。
- 上位フォルダーは配下件数のみ。
- 削除済み仮想ツリー。
- 場所不明仮想ルート。
- ShareとCloud Stateのサブアイコン。
- 色をUI固有値ではなく意味的状態として返す。
- Moveの移動元・移動先を同一関連操作IDで返す。
- Replay用にPaneの出現・更新・終了とイベント単位の時間軸を返す。

### フィルター

- 製品要件の全フィルター。
- Literal検索。
- AND、OR、除外。
- 保存可能なフィルターDTO。
- 将来Regexを差し込める契約。

## テスト

全製品特殊ケースのGolden Fixture、ページ境界、並び順、タイムアウト、別プロセス競合、大量グループ、期間diff。

## 受け入れ条件

- Projectionは再生成可能。
- Source Eventを変更しない。
- Avaloniaへ依存しない。
- 10万イベントのGroup生成ベンチマークfixtureを提供する。
- UIエージェントがFake Dataから利用できる。
