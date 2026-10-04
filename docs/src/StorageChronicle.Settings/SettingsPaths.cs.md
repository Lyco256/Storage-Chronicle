# SettingsPaths.cs

## 役割

設定モデルからWindows固有の保存先解決を分離する。Machine SettingsはProgramData配下、通常のUser SettingsはLocalAppData配下に置く。

## 公開型と不変条件

- `ISettingsPathProvider` はMachine/User設定のパスを公開する。
- `WindowsSettingsPathProvider` は製品が固定する相対ファイル名だけを付加する。
- `WindowsSettingsPathProvider.ForAuthenticatedUser(sid, localAppDataRoot)` はAgentが認証したSIDと、同じSIDに対応するとOS情報から解決したLocalAppDataルートを受け取り、ユーザー設定パスを導出する。SIDやパスをIPCペイロードから渡してはならない。
- SIDは数値形式のWindows SIDとして検査する。ルートは絶対ローカルパスで、UNC、相対パス、`.` / `..` セグメントを拒否する。SIDをファイル名に使わず、最終ファイル名は常に`Storage Chronicle/user-settings.json`とする。
- このSettings API自体はSIDの認証やprofile rootとの対応を検証しない。呼出元（特権Agent）がその信頼境界を満たすことが前提であり、任意のクライアント値を受理するAPIではない。

## 依存関係と失敗動作

`System.IO`とWindows special-folder APIを使う。SIDまたはプロフィールルートの形式が不正なら`ArgumentException`で拒否し、パスを作成・書込みする前に失敗する。Linux向けパスproviderは将来別実装とする。

## 関連テスト

Settings ????????????????????? ProgramData/LocalAppData ???????

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
