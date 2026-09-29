# DiffExplorerView.cs

## 役割

Explorer の8表示モードを共通 row source に投影し、ページ単位の bounded rows を返す。

## 公開型と不変条件

`SupportedModes` は ExtraLargeIcons、LargeIcons、MediumIcons、SmallIcons、List、Details、Tiles、Content の8値を返す。`GetPage` は one-based page を使用し、全行の Control を事前生成しない。連続ズームは整数百分率で50～300に制限し、初期値は100%。`SetZoomPercent` は範囲外を拒否し、`AdjustZoom` は境界へクランプする。表示方式を変えてもズームと共通projectionは維持される。

## 依存関係と失敗動作

`DiffViewModel.ExplorerRows` のみを読み取る。範囲外page/zoomは例外で拒否し、OS Explorerの起動は `IExplorerLauncher` に委譲する。

## 関連テスト

全8 mode、bounded page、zoomの連続値/上下限/範囲外入力の検証は `DiffViewModelTests.cs` に含まれる。
