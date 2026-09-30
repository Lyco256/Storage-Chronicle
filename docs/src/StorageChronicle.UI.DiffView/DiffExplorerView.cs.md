# DiffExplorerView.cs

## 役割

Explorer の8表示モードを共通 row source に投影し、ページ単位の bounded rows を返す。

## 公開型と不変条件

`SupportedModes` は ExtraLargeIcons、LargeIcons、MediumIcons、SmallIcons、List、Details、Tiles、Content の8値を返す。`GetPage` は one-based page を使用し、全行の Control を事前生成しない。連続ズームは整数百分率で50～300に制限し、初期値は100%。`SetZoomPercent` は範囲外を拒否し、`AdjustZoom` は境界へクランプする。`GetChildrenPage` は投影済みpathから直下の項目だけを返し、間の階層を共有する仮想folderを必要時にまとめて合成する。どちらもOS filesystemへアクセスしない。

## 依存関係と失敗動作

`DiffViewModel.ExplorerRows` のみを読み取る。範囲外page/zoomは例外で拒否し、synthetic grouping folderはunknown quality・Explorer open不可を維持する。OS Explorerの起動は `IExplorerLauncher` に委譲する。

## 関連テスト

全8 mode、bounded page、直下folder filteringと仮想中間folder、zoomの連続値/上下限/範囲外入力の検証は `DiffViewModelTests.cs` に含まれる。

## Role
Builds bounded Explorer rows from projected state for the eight supported layouts.

## Public types and responsibilities
`DiffExplorerView` provides layout identifiers, paging, direct-child filtering, and validated zoom. It does not acquire filesystem state.

## Inputs and outputs
Inputs are projected rows and page/layout settings. Outputs are bounded rows and virtual grouping rows derived only from projected paths. File contents and file-content hashes are never read.

## Dependencies
Depends on Diff View contracts and projected records. OS opening is delegated separately to `IExplorerLauncher`.

## Invariants
Paging is one-based and bounded; zoom is 50–300 percent; virtual grouping folders retain unknown quality and are not openable as real locations.

## Threading and lifetime
Rendering is synchronous and owns no background work or disposable lifetime.

## Failure behavior
Invalid pages and out-of-range zoom are rejected. Missing projected paths do not fabricate filesystem observations.

## Tests
See `tests/StorageChronicle.UI.DiffView.Tests/DiffViewModelTests.cs` for layouts, paging, grouping, and zoom boundaries.

## OS constraints
The renderer is platform-neutral and never enumerates the host filesystem.

## Change-sensitive contracts
Layout identifiers, page numbering, zoom bounds, and virtual-row quality/openability are compatibility contracts.
