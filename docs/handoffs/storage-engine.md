# feat/storage-engine handoff

## 変更範囲

- `src/StorageChronicle.Storage/**` に、CRC32C付き不変追記セグメント、閉じたセグメントのZstandard圧縮・展開検証、メタデータmanifest参照、末尾切り捨て、破損セグメントスキップを追加。
- 同ディレクトリに、WAL/FULL/foreign_keys/busy_timeout付きSQLite索引、明示migration、現在状態、path search、Process/Volume/Mount Session/Projection Cache、削除・破損後の再構築を追加。
- `tests/StorageChronicle.Storage.Tests/**` に実SQLite統合テストを追加。
- `docs/src/StorageChronicle.Storage/**` に各実装ファイルの説明を同期。
- `StorageChronicle.slnx` にStorage本体・テストを登録。共有契約は変更していない。

## 要件対応

Source/Canonical EventはJSON payloadとして追記ログにのみ正本保存し、Source EventはSQLiteなしでも読める。確定時のSHA-256はsegment ID、Sequence範囲、件数、圧縮状態、履歴ブランチのメタデータ参照であり、ファイル内容ハッシュではない。ログ削除API、確定ログ更新、ファイル内容読み出しは実装していない。

容量予約と実書込み失敗の両方を扱い、容量停止状態と最終Sequenceを保持する。周期flushは5秒既定、rename/move/delete/media removal/停止は優先flushし、flush失敗をStatusChangedと例外で上位へ返す。

## 検証コマンドと結果

- `dotnet restore tests/StorageChronicle.Storage.Tests/StorageChronicle.Storage.Tests.csproj` — 成功。
- `dotnet build tests/StorageChronicle.Storage.Tests/StorageChronicle.Storage.Tests.csproj --no-restore` — 成功、警告0。
- `dotnet test tests/StorageChronicle.Storage.Tests/StorageChronicle.Storage.Tests.csproj --no-build --no-restore` — 6件成功。

## 既知の制限

- `Microsoft.Data.Sqlite`の推移依存`SQLitePCLRaw.lib.e_sqlite3`にNuGet監査警告があるため、担当csprojでNU1903を抑制している。依存パッケージ更新は共有の`Directory.Packages.props`所有範囲のため、トップCodexで安全な版へ更新判断が必要。
- SQLiteのsnapshot APIは現在状態キャッシュを返す。過去時点の厳密な再生投影はState Engine担当との統合契約に委ねる。
- 100万件の性能fixtureは統合テストの外部性能工程で実行できるよう単一writer・逐次セグメント形式を採用しているが、本ブランチの高速テストでは小規模fixtureのみ実行した。

## 共有契約・競合

共有契約、Requirements、main/devenvは変更していない。トップCodexはこのブランチをレビュー後、`devenv`へno-ff統合すること。
