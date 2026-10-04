# SettingsPersistence.cs

## 役割

Machine SettingsとUser Settingsをversioned JSONとして保存し、UTF-8、同一ディレクトリ一時ファイル、flush、atomic replace、および直前世代の復旧を提供する。

## 公開型と不変条件

- `ISettingsFileSystem` は永続化操作を差し替え可能にし、`PhysicalSettingsFileSystem` が実ファイル操作を実装する。
- `ISettingsStore<T>` は設定の読込みと検証済み保存を提供する。
- `MachineSettingsStore` はMachine Settingsを保存する。
- `UserSettingsStore` はUser Settingsを保存する。既定コンストラクターとprovider指定コンストラクターは従来用途向け。Agentが認証済みクライアントの設定を扱う場合は`ForAuthenticatedUser`を使用し、認証トークンから取得したSIDと、そのSIDに一致するOS解決済みLocalAppDataだけを渡す。IPCのSID/パス値は使わない。
- `ForAuthenticatedUser` は固定の製品相対パスを合成し、SIDをパス要素として使わない。profile解決・SID認証そのものは呼出元の責務。
- versioned storeは不正なprimaryを`.bak`から復旧し、両世代が不正なら既定値と警告を返す。保存失敗時は一時ファイルを削除し、既存有効世代を保つ。

## 依存関係と失敗動作

`System.Text.Json`、`System.IO`、`SettingsValidator`、`ISettingsPathProvider`に依存する。パス、検証、読み書き、atomic replaceで発生した保存エラーは呼出元へ伝播する。破損した読込みは次世代または既定値へフェイルセーフ復旧する。

## 関連テスト

`tests/StorageChronicle.Settings.Tests/SettingsTests.cs` verifies UTF-8 persistence, schema/fallback behavior, corruption recovery, atomic-replace failure, and two authenticated user stores remaining isolated during normal writes and recovery.

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
