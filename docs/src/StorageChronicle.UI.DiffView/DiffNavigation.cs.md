# DiffNavigation.cs

## 役割

分割枠、Tree/Explorer の path navigation、Replay cursor/speed/play state を UI 内で保持する。

## 公開型と不変条件

`DiffSplitPaneState` は ratio を0.1～0.9に制限し、`DiffReplayState` は speed を0.25～16倍に制限する。Pane state は選択 path だけを持ち、projection や durable history を変更しない。

## 依存関係と失敗動作

外部依存はない。範囲外 ratio/speed は `ArgumentOutOfRangeException` で拒否する。

## 関連テスト

Headless テストが back/forward、split ratio、Move endpoint、Replay timeline を検証する。
