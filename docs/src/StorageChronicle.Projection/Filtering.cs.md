# Filtering.cs

リテラル検索の共通評価器を実装する。AllはAND、AnyはOR、Excludeは除外として評価し、名前、パス、拡張子、操作、プロセス、品質、ボリューム、削除、共有、照合差分、サイズなど製品要件のフィールドを同じDTOから扱う。

現在のmatcherは大文字小文字を区別しない部分文字列リテラルのみを実装する。Regexは保存形式を壊さず後から実装できるようIProjectionFilterMatcherに分離し、現実装では未対応例外にする。イベントとDiffで内容検索は行わず、ファイル内容やハッシュを読み取らない。

主なテストはDiffAndFilterTests.LiteralAndOrAndExclusionFiltersAreComposableと、Event StackのNormalized/Groupedフィルター経路。

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
