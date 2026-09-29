# DiffTreeView.cs

## 役割

path 投影を固定ガター付きの遅延 Tree に構成し、展開済み部分だけを可視行として返す。

## 公開型と不変条件

`DiffTreeNode` は child loader を保持し、`ExpandAsync` 前には descendants を materialize しない。仮想 ancestor、仮想削除、Unknown location root は表示可能だが、projection の事実と混同しないよう `IsVirtual` を保持する。`DiffTreeView` は全ノードを一括 Control 化せず、visible rows だけを返す。

## 依存関係と失敗動作

Projection の path resolver と FileDiffProjection に依存する。空の child loader は空行を返し、キャンセルは expansion 呼び出し元へ返す。

## 関連テスト

Headless テストが root、lazy expansion、virtual unknown/deleted row、gutter icon を検証する。
