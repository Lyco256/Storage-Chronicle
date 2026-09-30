# EventStackProjection.cs

Grouped node materialization indexes each group event once before building file and normalized descendants, avoiding repeated linear searches for large groups while preserving the complete descendant tree.

Source、Normalized、Groupedの3モードを生成する。GroupedはActivityをルートに、ファイル要約、Normalized Event、Source Eventの順で展開可能なEventStackNodeを作る。ProjectionPageはルートだけをページングし、子孫はルートと一緒に返すためページ境界で分断しない。

初期順序は新しいものが上で、Ascendingも選択できる。行ごとにProcess表示名と品質を保持し、UnverifiedGapはGap nodeとして独立する。SourceとNormalizedの関連は記録済みnormalizedEventIdだけを使い、推測やSource Eventの変更は行わない。

依存はDomain/ContractsとPath/Activity projectionのみ。主なテストは3モード、展開、並び順、ページング、Source/Normalized関連、Gap。

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
