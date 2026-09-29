# MFTを含む性能マトリクス受入れ要件

## 1. 目的

既存portable benchmarkだけでなく、実NTFS MFT enumerationを含む完全matrixを実行する。

## 2. 安全条件

MFT acceptanceでは `STORAGE_CHRONICLE_MFT_VOLUME` にホストC:を指定してはならない。

`37_PHYSICAL_READ_ONLY_ACCEPTANCE.md` の実機隔離境界内で新規作成・識別した `SC_TEST_MFT_VOLUME` のdevice pathだけを受け入れる。実機上の既存ボリュームをMFT datasetの作成先にしない。

benchmark起動前にmarker/TestRun GUIDを検証し、専用VHDXでない場合はfail-closedする。

## 3. MFT dataset

FileMutationWorkloadのbulk modeで0-byteファイル中心のMFT datasetを作る。

最低matrix:

- 10,000 entries
- 100,000 entries
- 1,000,000 entries

1Mは一つのdirectoryへ集中させず、複数階層・複数directoryへ分散する。

ファイル内容量で性能を稼がず、MFT/namespace規模を増やす。

## 4. 必須benchmark

既存の全portable suiteに加え、少なくとも次を実行する。

- actual MFT enumeration 10K
- actual MFT enumeration 100K
- actual MFT enumeration 1M
- MFT import + no-change reconciliation
- candidate 0件のmetadata query count
- candidate少数のreconciliation
- 1M state point-in-time path reconstruction
- large directory move
- Event Stack 100K
- Grouped 100K
- Period Diff
- append + SQLite index
- compressed segment close
- SQLite rebuild
- media manifest import/validation
- media segment append

## 5. 正確性併記

性能値だけではなく各runで次を保存する。

- dataset entry count
- enumerated entry count
- candidate count
- detailed metadata query count
- generated canonical count
- dropped event count
- memory allocation
- elapsed/mean
- environment
- OS/build
- 物理host CPU/memory
- VHDX type/size

件数不一致やevent dropがあれば性能が速くてもFAIL。隔離VHDXの識別情報と監査した書込み先を併記する。

## 6. 閾値

このフェーズでは50MiB/0.5%以外の新しい絶対速度閾値を勝手に作らない。

性能matrixの必須条件は、全suiteが実行され、OOM/timeout/crash/欠落なしで結果が生成されること。

既存baselineに対する大幅退行は結果に明示し、原因を解析する。ユーザーが設定していない arbitrary thresholdでリリース可否を決めない。

## 7. 負荷最小化

1M datasetは一度作成後にrun専用ルート内の識別済みMFT seed VHDXとして保存してよい。既存の別用途VHDXをseedへ転用しない。

各benchmarkで1Mファイルを作り直さず、read-onlyの測定では検証済みseedを使う。変更を伴う測定はrun専用の新規コピーに限る。

測定対象へ影響するためbenchmark中に圧縮、Defender exclusion変更、host power plan変更を勝手に行わない。

## 8. 完了条件

`build/quality/Test-FullBenchmarkMatrix.ps1 -IncludeMft` 相当の最終scriptが37の監査済み実機専用VHDXで全必須suiteを実行し、manifestが `AcceptanceEligible=true` になること。
