# DiffViewModel.cs

## 役割

共通 Projection の rich diff projection を一度だけ取得し、Tree と Explorer に同一行・同一選択・同一ガター情報を提供する Headless UI 状態である。stable `IProjectionService` しか持たない IPC/テストホスト向けにはローカル adapter を使用する。

## 公開型と不変条件

- `DiffViewModel` は Live、Period、PointInTime、Replay を保持し、Live pause 中は UI refresh だけを止め Agent の記録を止めない。
- Activity Frame要約を独立paged sourceから保持し、新しい順を標準にサーバー側でページ選択前に昇順/降順を適用する。順序切替は先頭ページを再取得し、次ページ取得はDiff rowページと独立する。pinしたFrameはLive timeoutで取得対象から外れた後も保持する。Replayでは選択中Frameのmetadata-onlyイベントtimelineを再生cursorへ接続し、cursorがStartedUtcからCloseBoundaryUtcに入る間だけ未pin Frameを表示する。pin状態はReplay消滅判定を越えて維持する。
- Explorerの8表示と50～300%連続ズーム、分割枠、back/forward/up/breadcrumb/direct-path履歴、Move相互移動、Replay cursor/イベント送り/1x・2x・10x speed/pause-resume/present、Explorer可否を一元管理する。`NavigateToLocation` は実パスの存在確認をせず、`preserveSelection` 指定時以外は対象行がない場所へ移動する時に選択を解除する。
- `Rows` は既存 shared contract の `DiffEntry`、`Items`/`ExplorerRows` は rich projection とその表示情報であり、UI が OS filesystem を直接読むことはない。

## 依存関係と失敗動作

`IProjectionService` または `IDiffProjectionSource` と、注入可能な `IExplorerLauncher` に依存する。path navigation/breadcrumb/historyは投影済み仮想場所を含め文字列上で処理し、OS filesystemへ問い合わせない。仮想削除行・不明場所・disabled row は Explorer launcher を呼ばず、理由を表示可能な状態として返す。`ReturnReplayToPresent` は現在位置へ移動してReplayを停止する。キャンセルはprojection/launcherへ伝播する。

## 関連テスト

`tests/StorageChronicle.UI.DiffView.Tests/DiffViewModelTests.cs` が8表示、Tree、Explorer、zoom範囲、path breadcrumb/up/direct/back/forward、仮想削除場所、履歴分岐、即時子項目と合成folder、split、Move、Live pause、Replay速度・step境界・停止中のpresent復帰、pause/resume、Frameの独立ページング/新しい順/並べ替え/pin保持と解除/Replay lifecycle、キャンセル/タイムライン不在を検証する。

## Role
Coordinates projection queries and exposes their results as UI-neutral Diff View state.

## Public types and responsibilities
`DiffViewModel` owns selected time mode, current projected rows, navigation state, and replay/pause presentation controls. It does not own canonical recording.

## Inputs and outputs
Inputs are projection responses and user navigation/replay actions. Outputs are view state and status; source quality remains distinct from UI grouping.

## Dependencies
Uses shared projection services/contracts and Diff View rendering/navigation helpers. Desktop controls and OS launch behavior remain outside the model.

## Invariants
Live display pause does not stop Agent recording. Replay changes the displayed cursor only. Projection failure is not converted into an empty successful observation.

## Threading and lifetime
Projection requests are asynchronous and cancellation-aware; callers own the model lifetime and cancellation scope.

## Failure behavior
Query errors and cancellation remain observable; a failed or cancelled refresh cannot be reported as a successful current projection.

## Tests
See `tests/StorageChronicle.UI.DiffView.Tests/DiffViewModelTests.cs` for modes, replay, pause, moves, errors, and cancellation.

## OS constraints
The model is platform-neutral and does not access the host filesystem.

## Change-sensitive contracts
Time modes, replay cursor semantics, Live pause behavior, and failure/cancellation reporting are compatibility-sensitive.
