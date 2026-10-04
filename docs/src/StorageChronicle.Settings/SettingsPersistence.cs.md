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

`tests/StorageChronicle.Settings.Tests/SettingsTests.cs` の全項目往復、schema/未知フィールド、primary/backup破損、両世代破損、atomic replace失敗、ユーザー別分離・回復テスト。
