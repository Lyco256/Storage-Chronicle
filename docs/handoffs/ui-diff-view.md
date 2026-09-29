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

## 既知の制限

- Avalonia Desktop rendering・Desktopへのproject登録/統合は担当外（top agent所有）。この変更はplatform-neutral/headless contractまで。
- direct-path navigationは文字列履歴であり、実在確認・filesystem query・OS Explorer起動を行わない。Explorer起動eligibilityは共通Projectionの事実に従う。
- zoom値はUI状態/APIのみ。Avalonia側での実寸レンダリング反映は統合後に接続する。
