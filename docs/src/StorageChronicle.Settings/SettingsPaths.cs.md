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

`tests/StorageChronicle.Settings.Tests/SettingsTests.cs` のパス要件、2ユーザー保存先分離、不正SID、不正プロフィールルート、およびユーザー別破損復旧テスト。
