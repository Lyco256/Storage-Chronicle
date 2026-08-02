# サブエージェント要件：Integration・Quality

## ブランチ

`feat/integration-quality`

## 開始条件

Wave 2統合済み。

## 所有パス

- `tests/StorageChronicle.Architecture.Tests/**`
- `tests/StorageChronicle.Integration.Tests/CrossModule/**`
- `tests/StorageChronicle.WindowsIntegration.Tests/**`
- `tests/StorageChronicle.UI.Headless.Tests/**`
- `tests/StorageChronicle.EndToEnd.Tests/**`
- `benchmarks/StorageChronicle.Benchmarks/**`
- `tools/StorageChronicle.TestDataGenerator/**`
- `tools/StorageChronicle.LogInspector/**`
- `build/quality/**`
- 対応する`docs/build/quality/**`、`docs/tools/**`、`docs/benchmarks/**`
- `docs/handoffs/integration-quality.md`

既存機能の製品コード修正は行わず、問題をハンドオフへ列挙する。トップCodexが修正する。

## 実装要件

- 全Architecture Gate。
- Golden Fixture。
- E2E Fake Collector。
- 実SQLiteと追記ログ。
- Windows privilegedスイート。
- UI Headless横断。
- 破損復旧。
- 容量不足。
- Agent再起動。
- 外付け媒体分岐。
- Windows 10互換テスト定義。
- メモリ・CPU測定ハーネス。
- BenchmarkDotNet。
- Log Inspector。
- 大量テストデータ生成。
- Doc Mirror Validatorを統合ゲートへ追加。
- 全テストのカテゴリと実行時間を文書化。
- `Requirements/98_REQUIREMENTS_COVERAGE.md`の各行へ対応する自動または手動受け入れ検証IDを付けた検証表を`docs/release/requirements-verification.md`へ生成する。

## Windows 10

今は実機実行できなくても、次を個別テストとして用意する。

- OS能力検出。
- Windows 11専用APIが未検出時に呼ばれない。
- USN。
- MFT列挙。
- ETW。
- Clipboard listener。
- Share。
- Cloud placeholder API。
- Avalonia起動。
- Service。

Windows 10で主要機能が実装上不可能と判明した場合、勝手にWindows 11限定へせず、具体的API、失敗理由、代替案をトップCodexへ報告する。実機未実行と実行成功を区別した`docs/release/windows10-compatibility.md`を生成し、未実行状態を「互換確認済み」と表現しない。

## 受け入れ条件

- `Test-All`が一コマンドで動く。
- 特権テストが明確に分離。
- 失敗時に原因が分かる。
- flakyなsleep依存テストがない。
- 性能結果を`docs/release/performance-baseline.md`へ生成できる。
- 定義済みアイドル測定で50MiBまたは0.5%を超えた場合、テストは失敗し、ユーザー例外承認なしにmain-readyとしない。
