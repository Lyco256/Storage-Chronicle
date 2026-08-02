# DiffViewContracts.cs

## 役割

共通 Projection と UI の間の local adapter、gutter visual token、Explorer row、Explorer launcher の platform-neutral 契約を定義する。

## 公開型と不変条件

`IDiffProjectionSource` は rich `DiffProjection` を Tree と Explorer へ共有する。`ContractDiffProjectionSource` は既存 `IProjectionService` の `DiffEntry` を表示用に包むだけで、ファイル内容を取得しない。`DiffVisuals` は semantic state を stable color/icon token に変換し、`IExplorerLauncher` は現在存在し open 可能な行にだけ呼び出される。

## 依存関係と失敗動作

Domain、Contracts、Projection の公開型だけに依存する。仮想・削除・不明場所は `ExplorerOpenResult.Rejected` で拒否し、headless default launcher は OS I/O を行わない。

## 関連テスト

DiffViewModel の Headless テストが adapter 経由の rich rows、visuals、Explorer launcher の可否を検証する。
