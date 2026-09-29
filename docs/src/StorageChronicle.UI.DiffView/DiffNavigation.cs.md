# DiffNavigation.cs

## 役割

分割枠、Tree/Explorer の path navigation、Replay cursor/speed/play state を UI 内で保持する。

## 公開型と不変条件

`DiffSplitPaneState` はratioを0.1～0.9に制限する。Pane stateは表示pathだけを持ち、projectionやdurable historyを変更しない。

`DiffPathNavigation` は `/` と `C:\`/UNC形式のpathを文字列として処理し、filesystemへアクセスせずroot-to-leaf breadcrumb、親path、直接入力のlexical validationを提供する。相対path、`.`/`..` segment、NULは直接入力として拒否する。投影済みの削除済み/仮想場所は表示pathそのもので移動できる。

`DiffReplayState` が許可する速度は正確に1x/2x/10x。`DiffViewModel.StepReplay(-1|1)` は選択項目timelineを一イベント進退し、timelineの不在または両端では `false`。`ReturnReplayToPresent` は最後のprojection query終端時刻を示しpresent状態を設定する。

## 依存関係と失敗動作

外部依存はない。path処理は純粋な文字列操作で、範囲外ratio/speedや不正step幅は `ArgumentOutOfRangeException` で拒否する。存在しないが絶対pathとして妥当な場所もローカル履歴へ登録でき、back/forwardはprojection sourceやOSへ問い合わせない。

## 関連テスト

Headlessテストがbreadcrumb/up/direct path、仮想/削除pathと分岐履歴、split ratio、Move endpoint、Replay速度・step境界・present・pause/resume、projection cancellationを検証する。

## Role
Contains platform-neutral path navigation, split-pane state, and replay cursor state for Diff View.

## Public types and responsibilities
`DiffPathNavigation` manages lexical paths and navigation history; `DiffSplitPaneState` validates pane proportions; `DiffReplayState` tracks playback state without changing recorded history.

## Inputs and outputs
Inputs are path strings, navigation commands, pane ratios, timeline positions, and replay speeds. Outputs are breadcrumbs, current/parent paths, and validated view state; no file content or content hash is involved.

## Dependencies
Uses platform-neutral Diff View contracts and standard runtime types; it does not call the Agent or filesystem.

## Invariants
Path handling is lexical. Replay and navigation alter view state only. Supported speeds are 1×, 2×, and 10×.

## Threading and lifetime
Transitions are synchronous and caller-owned; these state objects retain no timer or background task.

## Failure behavior
Malformed input and invalid ratios, speeds, or step sizes are rejected; unavailable endpoint operations report false.

## Tests
See `tests/StorageChronicle.UI.DiffView.Tests/DiffViewModelTests.cs` and `tests/StorageChronicle.UI.Headless.Tests`.

## OS constraints
No host path-existence checks or OS filesystem APIs are used; supported absolute path forms are handled as strings.

## Change-sensitive contracts
Accepted path forms, replay speeds, split-ratio bounds, and endpoint behavior are compatibility-sensitive.
