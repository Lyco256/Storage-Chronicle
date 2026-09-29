# ProjectionModels.cs

Projection層の公開DTOと設定契約を定義する。ProjectionDocumentはCanonical Eventと任意のSource Eventをコピーして保持し、Projection再生成時に入力を変更しない。EventStackNodeはActivity、ファイル要約、Normalized、Source、Gapの展開階層を表し、Groupedページの子を同じルートに閉じ込める。

ProjectionSettingsはActivity timeout（0.5–60秒、既定2秒）とEvent Stack page size（50–5000、既定250）を検証する。FileDiffProjectionとDiffProjectionはTree/Explorer共通のDiff結果を表し、主操作、サブ操作、品質、仮想項目、Replay timelineをUI値ではなく意味的な値で返す。ProjectionFilterは保存可能なAND/OR/除外DTOと将来のRegex matcher差し込み契約を提供する。

依存はDomain契約のみで、Avalonia、ファイルシステム、イベントストアを参照しない。無効なページ、timeout、空フィルター値は例外にし、履歴削除や内容保存の型は持たない。主なテストはEventStackProjectionTests、DiffAndFilterTests、ResilienceAndScaleTests。

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
