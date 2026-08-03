# EventStackView.axaml

Renders the three Event Stack modes with a virtualized top-level page and independently expandable child levels. Operation and quality icons remain UI interpretation; source facts and quality text come from the projection view model.

## 役割

Compiled Bindingを有効にしたAvalonia実UI。モード、順序、Live、filter、保存filter、virtualized rows、展開children、詳細品質、process navigation、ページ操作を表示する。

## アクセシビリティ・入力

行・filter・ページ・詳細・processリンクにAutomationProperties名とtooltipを付ける。View code-behindがAvalonia KeyをUI中立`EventStackKey`へ変換し、ViewModelが操作を処理する。

## 不変条件・関連テスト

行リストはVirtualizingStackPanelを使用し、表示ページ外の行を生成しない。Headless Avaloniaテストが実Viewをmeasure/arrangeし、RowsListとcompiled bindingのDataContextを確認する。

## Role

This mirror documents the source boundary for this file and explains how it participates in Storage Chronicle.

## Public types and responsibilities

Public types preserve source facts and the explicitly owned responsibility; UI interpretation and correlation remain outside this boundary.

## Inputs and outputs

Inputs and outputs are the declared contracts of the source file. File contents and file-content hashes are never an input or output.

## Dependencies

Dependencies are limited to the referenced project contracts and platform services shown by the source file.

## Invariants

The source keeps canonical facts distinguishable from reconstructed state and does not synthesize descendant events.

## Threading and lifetime

Callers own cancellation and lifetime; asynchronous work must not outlive the owning pipeline or UI scope.

## Failure behavior

Failure, corruption, cancellation, and recovery remain observable and are not converted into a false successful observation.

## Tests

Validated by tests/StorageChronicle.Integration.Tests and the affected integration tests.

## OS constraints

Platform-neutral behavior remains portable; Windows-only APIs are isolated in the Windows platform projects.

## Change-sensitive contracts

Public names, serialized fields, persistence boundaries, and the mirrored path are compatibility-sensitive contracts.
