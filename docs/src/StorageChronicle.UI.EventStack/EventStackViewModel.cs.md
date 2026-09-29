# EventStackViewModel.cs

The initial display mode, sort order, page size, and named saved filters load and persist through the shared Agent-backed `IUserSettingsClient`. Mode and sort changes, page-size changes, and saving a named filter update only the current user's settings. Legacy plain-text saved filters are accepted and serialized in the versioned typed-filter format on the next save. Legacy initial-mode numeric values are interpreted by the canonical Settings contract; source and normalized remain distinct while legacy activity/file modes resolve to Grouped.

## 役割

Avalonia/MVVM Event Stackの相互作用状態を所有する。Source、Normalized、Groupedの3モード、同一filter/sort query、展開、ページング、選択、詳細、Live追従、キーボード操作をまとめる。

## 不変条件

- 基本初期モードはGrouped、初期順序はDescending（新しいものが上）。User Settingsからページサイズと並び順を読み込む。
- `LoadPageAsync`は投影境界から要求ページだけを受け取り、その件数だけ`EventStackItemViewModel`を生成する。10万件を全件VM化しない。
- Groupedの子は`EventStackPage`の同一グループ内に返され、ViewModelはページ境界で子を分断しない。
- Source Eventは書換えず、Storage/Windows APIは参照しない。User Settingsは`IUserSettingsClient`経由でAgent IPCから取得・更新する。
- ページ行数は50～5000の任意の整数。保存済みフィルターは名前とfilter DTOをversion付き文字列としてUser Settingsへ保存し、旧文字列形式も検索語filterとして読み込む。
- Live追従は過去スクロールで停止し、Current操作で1ページ目へ戻って再開する。

## 公開状態・操作

品質、Origin、記録時刻、ローカルオフセット、Source/Mount Sequence、親/子Processを詳細に表示できる。`EventStackKey`を介して上下、左右展開、ページ移動、Home/End、Escapeを処理する。ページサイズと名前付き保存済みfilterの読み書き、Material Iconsの意味ID解決もUI境界で扱う。

## 依存関係・失敗動作

CommunityToolkit.Mvvm、Domain/Contracts/UI.Shared/Settingsのプラットフォーム中立型に依存する。ページ取得と設定操作はasyncのみで、キャンセルは投影/Agent境界へ渡す。Agent未接続や設定拒否はStatusMessageで可視化し、UIが同期ファイルI/Oやファイル直接更新をしない。

## 関連テスト

`EventStackViewModelTests`が3モード、選択維持、展開、ページ境界、順序、Live、filter、品質、未知Process、10万件ページ生成、キーボード、任意ページ行数、User Settings読み込み・保存・旧filter互換を検証する。
