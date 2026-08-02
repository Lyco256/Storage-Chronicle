# DiffExplorerView.cs

## 役割

Explorer の8表示モードを共通 row source に投影し、ページ単位の bounded rows を返す。

## 公開型と不変条件

`SupportedModes` は ExtraLargeIcons、LargeIcons、MediumIcons、SmallIcons、List、Details、Tiles、Content の8値を返す。`GetPage` は one-based page を使用し、全行の Control を事前生成しない。

## 依存関係と失敗動作

`DiffViewModel.ExplorerRows` のみを読み取る。範囲外 page は例外で拒否し、OS Explorer の起動は `IExplorerLauncher` に委譲する。

## 関連テスト

全8 mode と bounded page の検証は `DiffViewModelTests.cs` に含まれる。
