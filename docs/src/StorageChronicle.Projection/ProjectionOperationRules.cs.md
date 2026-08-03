# ProjectionOperationRules.cs

Canonical operationをActivity/Diffの主操作へ決定的に分類する。Delete、Recycle、Restore、Move、Copy、Create、Rename、DataWrite系、Share、Cloud、Metadataの順序を固定し、意味的なDiff状態へ変換する。

read-only ETW観測はDurable Eventの履歴を変更せずActivity境界で分断しない。Unknown/Gapは独立表示の入力として扱い、Copyは記録済みプロパティの厳密な意図がある場合だけ選ぶ。UI色やAvalonia型は持たない。

主なテストはDiff優先順位、移動元/移動先の関連ID、UnverifiedGap独立表示。

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
