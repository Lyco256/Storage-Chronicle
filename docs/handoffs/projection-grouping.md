# Projection・Grouping handoff

## ブランチと所有範囲

- ブランチ: feat/projection-grouping
- 変更範囲: src/StorageChronicle.Projection/**、tests/StorageChronicle.Projection.Tests/**、対応するdocs/src/StorageChronicle.Projection/**、このハンドオフ、solutionへのProjection project参照
- Domain/Contractsの共有契約は変更していない。

## 実装内容

- Source / Normalized / Grouped Event Stackと、GroupedのActivity→file summary→Normalized→Source展開
- 新しいものが上、Ascending/Descending、ルート単位ページング、Grouped子のページ境界分断防止
- Process Instance表示、親Process/子Processの記録済み関係、Explorer操作、品質表示、独立UnverifiedGap
- 製品要件15章のActivity timeout、Unknown actorキー、read非分断、競合Process境界、表示ルートアンカー
- Live / Period / Point-in-Time / Replayの共通Diff projection
- Delete優先順位、作成後削除、Rename履歴、Move関連ID、移動後編集、仮想削除/場所不明ルート、Share/Cloudサブ操作、Replay timeline
- 製品要件17章のliteral、AND、OR、除外、保存可能DTO、将来Regex matcher契約
- 100,000イベントの再生成fixtureとGolden相当の特殊ケーステスト

Projectionは入力イベントとSource Eventを変更せず、ファイル内容・内容ハッシュ・OS APIを読み取らない。TreeとExplorer向けには同じFileDiffProjectionを返す。

## 検証

実行したコマンド:

    dotnet build src\StorageChronicle.Projection\StorageChronicle.Projection.csproj --no-restore
    dotnet test tests\StorageChronicle.Projection.Tests\StorageChronicle.Projection.Tests.csproj --no-restore

結果:

- Projection build: 成功、0 warnings、0 errors
- Projection tests: 13 passed、0 failed、0 skipped
- 100,000 canonical events: Group生成と入力EventId不変性を検証済み

## 既知の制約

- Regexは将来のIProjectionFilterMatcher差し込み契約だけを用意し、MVPのリテラルmatcherでは未対応例外にする。
- パスは記録済みpath/parent関係から再構成し、Projection層からファイルシステムへ問い合わせない。
- solution全体の他担当プロジェクトに存在する未解決ビルド問題はこの担当範囲外であり、Projection project単体の警告ゼロ検証には影響しない。
