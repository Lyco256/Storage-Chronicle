# feat/ui-diff-view handoff

## 変更

- `StorageChronicle.UI.DiffView` を共通 Projection の rich `DiffProjection` に接続し、stable `IProjectionService` fallback adapter も提供。
- Tree renderer: 固定ガター、primary/sub semantic icon/color token、path ancestor、仮想削除、Unknown location root、遅延 descendant 展開、visible row projection。
- Explorer renderer: ExtraLargeIcons/LargeIcons/MediumIcons/SmallIcons/List/Details/Tiles/Content の8表示、bounded page、共有 row/selection、Explorer 可否と disabled 理由。
- UI state: Live/Period/PointInTime/Replay、Live pause/resume、Replay timeline/cursor/speed、path back/forward、分割枠、Move reciprocal navigation。
- OS filesystem は直接参照せず、既存 item の open は `IExplorerLauncher` adapter に限定。
- Headless tests と `docs/src/StorageChronicle.UI.DiffView/**` 対応文書を追加。

## 共有契約

共有契約は変更していない。UI project が common `StorageChronicle.Projection` の公開 rich projection を参照し、既存 `IProjectionService` も fallback として利用する。

## 検証

- `dotnet restore tests/StorageChronicle.UI.DiffView.Tests/StorageChronicle.UI.DiffView.Tests.csproj` — 成功
- `dotnet build src/StorageChronicle.UI.DiffView/StorageChronicle.UI.DiffView.csproj --no-restore` — 成功、警告0
- `dotnet build tests/StorageChronicle.UI.DiffView.Tests/StorageChronicle.UI.DiffView.Tests.csproj --no-restore` — 成功、警告0
- `dotnet test tests/StorageChronicle.UI.DiffView.Tests/StorageChronicle.UI.DiffView.Tests.csproj --no-restore` — 6/6 成功

## 統合時の注意

共有所有の solution への project entry は統合担当が追加すること。

- `src/StorageChronicle.UI.DiffView/StorageChronicle.UI.DiffView.csproj`
- `tests/StorageChronicle.UI.DiffView.Tests/StorageChronicle.UI.DiffView.Tests.csproj`

統合後に solution 全体の build、Headless UI suite、Projection integration を再実行すること。
