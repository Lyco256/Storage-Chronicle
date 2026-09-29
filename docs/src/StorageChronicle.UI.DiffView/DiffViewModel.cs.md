# DiffViewModel.cs

## 役割

共通 Projection の rich diff projection を一度だけ取得し、Tree と Explorer に同一行・同一選択・同一ガター情報を提供する Headless UI 状態である。stable `IProjectionService` しか持たない IPC/テストホスト向けにはローカル adapter を使用する。

## 公開型と不変条件

- `DiffViewModel` は Live、Period、PointInTime、Replay を保持し、Live pause 中は UI refresh だけを止め Agent の記録を止めない。
- Explorerの8表示と50～300%連続ズーム、分割枠、back/forward/up/breadcrumb/direct-path履歴、Move相互移動、Replay cursor/イベント送り/1x・2x・10x speed/pause-resume/present、Explorer可否を一元管理する。
- `Rows` は既存 shared contract の `DiffEntry`、`Items`/`ExplorerRows` は rich projection とその表示情報であり、UI が OS filesystem を直接読むことはない。

## 依存関係と失敗動作

`IProjectionService` または `IDiffProjectionSource` と、注入可能な `IExplorerLauncher` に依存する。path navigation/breadcrumb/historyは投影済み仮想場所を含め文字列上で処理し、OS filesystemへ問い合わせない。仮想削除行・不明場所・disabled row は Explorer launcher を呼ばず、理由を表示可能な状態として返す。キャンセルはprojection/launcherへ伝播する。

## 関連テスト

`tests/StorageChronicle.UI.DiffView.Tests/DiffViewModelTests.cs` が8表示、Tree、Explorer、zoom範囲、path breadcrumb/up/direct/back/forward、仮想削除場所、履歴分岐、split、Move、Live pause、Replay速度・step境界・present・pause/resume、キャンセル/タイムライン不在を検証する。
