# MaterialOperationIconResolver.cs

## 役割

Domainの`OperationIconMeaning`をMaterial Iconsの安定した意味IDへ解決する。Create/Edit/Move/Rename/Delete/ReconcileなどをUIで一貫表示する。

## 不変条件・依存関係

操作名を直接UI文字列へ散在させず、`IOperationIconResolver`として注入可能にする。DomainとUI.Sharedだけに依存し、Storage/Windows APIは参照しない。

## 関連テスト

各行の`IconKey`生成とHeadless実UIの行バインディングで使用される。

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
