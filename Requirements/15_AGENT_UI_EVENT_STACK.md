# サブエージェント要件：UI Event Stack

## ブランチ

`feat/ui-event-stack`

## 所有パス

- `src/StorageChronicle.UI.EventStack/**`
- `tests/StorageChronicle.UI.EventStack.Tests/**`
- 対応する`docs/src/StorageChronicle.UI.EventStack/**`
- `docs/handoffs/ui-event-stack.md`

## 目的

Source、Normalized、Groupedの3表示モードを同じProjectionと検索状態で切り替える仮想化Event Stackを実装する。

## 実装要件

- CommunityToolkit.Mvvm。
- Compiled Binding。
- ViewModelにStorageまたはWindows APIを入れない。
- 初期モードGrouped。
- Source、Normalized、Groupedを即時切替え。
- Groupedを操作グループ、ファイル要約、Normalized、Sourceへ展開。
- 初期順序は新しいものが上。
- 昇順・降順。
- 一ページ最大行数設定。
- メール一覧型ページング。
- 展開子をページ境界で分断しない。
- Live自動追従。
- 過去スクロールで追従停止。
- 現在へ戻る操作。
- Event Stackではプロセス名を行表示。
- Exact/Correlated/Unknownは詳細表示。
- 詳細パネルから記録済み親プロセスと子プロセスへ移動できる。
- Source Origin、品質、時刻、ローカルオフセット、Sequenceの詳細。
- Reconciliation、UnverifiedGap、ExistenceOnlyの明確な表示。
- フィルターと保存済みフィルターUI。
- 行仮想化。
- キーボード操作。
- ツールチップとアクセシビリティ名。
- Material Iconsを意味IDから解決する。

## テスト

Avalonia Headlessで：

- 3モード。
- 選択維持。
- 展開。
- ページング。
- 並び順。
- 自動追従。
- フィルター。
- Correlated表示。
- 不明プロセス。
- 大量行仮想化で全ViewModelを生成しないこと。

## 受け入れ条件

- Fake Projectionだけで動作する。
- UIスレッドで同期I/Oしない。
- Source EventをUIが書換えない。
- 10万件データでもページ取得範囲だけを表示用に生成する。
