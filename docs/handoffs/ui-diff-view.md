# feat/ui-diff-view handoff

## 変更

- `StorageChronicle.UI.DiffView` を共通 Projection の rich `DiffProjection` に接続し、stable `IProjectionService` fallback adapter も提供。
- Tree renderer: 固定ガター、primary/sub semantic icon/color token、path ancestor、仮想削除、Unknown location root、遅延 descendant 展開、visible row projection。
- Explorer renderer: ExtraLargeIcons/LargeIcons/MediumIcons/SmallIcons/List/Details/Tiles/Content の8表示、bounded page、共有 row/selection、50～300%連続zoom、Explorer 可否と disabled 理由。
- UI state: Live/Period/PointInTime/Replay、Live pause/resume、Replay timeline/cursor/1x・2x・10x speed/一イベントstep/present移動、直接path・breadcrumb/up/back/forward（仮想/削除path含む）、分割枠、Move reciprocal navigation。
- OS filesystem は直接参照せず、既存 item の open は `IExplorerLauncher` adapter に限定。
- 追加headless tests はzoom境界/不正値、direct path入力検証、breadcrumb/up、仮想削除path、履歴のback-forward/分岐、Replay speed選択/step境界/present、projection cancellation/タイムライン不在を検証。
- `docs/src/StorageChronicle.UI.DiffView/**` の実装内容・公開API説明を同期。

## 共有契約

共有契約は変更していない。UI project が common `StorageChronicle.Projection` の公開 rich projection を参照し、既存 `IProjectionService` も fallback として利用する。

## 検証

- `dotnet test "C:\Users\lyco2\.codex\worktrees\diff-view-core\Storage Chronicle\tests\StorageChronicle.UI.DiffView.Tests\StorageChronicle.UI.DiffView.Tests.csproj"` — 成功、10/10（SDK 10.0.401）
- `dotnet run --project "C:\Users\lyco2\.codex\worktrees\diff-view-core\Storage Chronicle\tools\StorageChronicle.DocMirrorValidator\StorageChronicle.DocMirrorValidator.csproj" -- "C:\Users\lyco2\.codex\worktrees\diff-view-core\Storage Chronicle"` — 成功、Doc mirror validation passed
- `build/Test-Fast.ps1 -NoRestore` — 成功（full solution fast suite、SDK 10.0.401。DiffView 10/10を含む）
- `build/quality/Test-DocMirror.ps1` — no-restoreではvalidator assets不足で失敗。上記restore付き `dotnet run` で同等のDocMirror validatorを成功確認。
- SDK注意: `global.json`指定の10.0.302はこの環境に未導入。設定ファイルは変更せず、SDK解決に影響しない `C:\` をworking directoryとしてコマンド実行。

## 統合時の注意

共有所有の solution への project entry は統合担当が追加すること。

- `src/StorageChronicle.UI.DiffView/StorageChronicle.UI.DiffView.csproj`
- `tests/StorageChronicle.UI.DiffView.Tests/StorageChronicle.UI.DiffView.Tests.csproj`

統合後に solution 全体の build、Headless UI suite、Projection integration を再実行すること。

## トップエージェント統合

統合側で `DiffProjectionPanel` をAgent settings IPCへ接続し、ズーム設定を製品の通常設定APIで保存、Explorerのpath/history/breadcrumbと直下項目表示をAvaloniaへ接続した。Replay再生は経過時間を1x/2x/10xでtimelineへ適用し、イベント送りはモデルの一件stepを使う。Desktop表示/操作面を検証するheadless UIテストを追加した。

- 統合時のDiff View core tests: 11/11 pass。
- `dotnet build src/StorageChronicle.UI.Desktop/StorageChronicle.UI.Desktop.csproj --no-restore`: 0 warnings/errors。
- `dotnet test tests/StorageChronicle.UI.Headless.Tests/StorageChronicle.UI.Headless.Tests.csproj --no-restore`: 8/8 pass。
- 最終統合後 `./build/Test-All.ps1` — 2026-09-29 成功、exit code 0。DiffView 11/11、Headless UI 8/8、設定UI 13/13を含む。全体カバレッジ/文書ミラー/VirtualBox契約/UIゲートも成功。

トップ統合追補（2026-09-30）: Requirement 00 §16.3 のrestart/replay欠落を補い、ProcessStopを非ファイルの耐久ProcessLifecycleEventとして保存し、ReplayではAgent再起動後も読み戻す。サーバーはsort方向を適用してからページを選ぶ。Storage 15/15、Agent 48 passed/3 physical skips、既定の `./build/Test-All.ps1` exit 0、DocMirror pass。実機・elevated受入は未実行。

## 既知の制限

- プロセス/Activity別のFrame、開始/終了/timeoutのライフサイクル、pin/order、複数pane表示は実装・テスト済み。ETW ProcessStopは別種の非ファイル履歴として耐久化し、Agent再起動後のReplayで復元する。Windows実機受入は未実施。
- direct-path navigationは文字列履歴であり、実在確認・filesystem query・OS Explorer起動を行わない。Explorer起動eligibilityは共通Projectionの事実に従う。
- zoom値はUI状態/APIのみ。Avalonia側での実寸レンダリング反映は統合後に接続する。

## トップエージェント統合追補: Activity Frames

- shared `IDiffProjectionSource` は `DiffProjectionBundle` を返し、Diff row projectionとActivity Frame metadata pageを分離した。UIはAgent DTOの重複型を作らず、既存Runtime contractを使用する。
- ViewModelはFrame一覧を新しい順に表示し、昇順切替時はAgentが並べ替えた先頭ページを取り直す。Sort directionはIPCへ渡り、サーバー側で全Frameをsortしてから独立pagingするため、履歴が1ページを超えても新着が先頭となる。独立追加読込、Frame event timeline、Live timeout後のpin維持/解除を実装した。Replay cursorで未固定Frameの出現・消滅を絞る。
- Avalonia panelはFrameごとにプロセス名、route、時間、duration、change/file count、size delta、操作内訳、stateを表示し、selected Frameだけのmetadata-only event timelineを追加pagingする。
- UI ViewModel test suite: `dotnet build tests/StorageChronicle.UI.DiffView.Tests/StorageChronicle.UI.DiffView.Tests.csproj --no-restore --nologo` (0 warnings/errors), `StorageChronicle.UI.DiffView.Tests.exe --progress off --minimum-expected-tests 1` (12/12 passed)。Desktop project build: 0 warnings/errors。
- LiveではWindows ETW ProcessStopをProcess Instance IDへ対応づけ、Source/Canonical file historyとは別のProcessLifecycleEventとして保存し、Agent内transient registryも更新してFrameを閉じる。Replayはdurable lifecycle indexを用いる。未知PIDは推測せず無視する。Storage/Agent restart-replay tests pass; physical ETW acceptance remains pending.
