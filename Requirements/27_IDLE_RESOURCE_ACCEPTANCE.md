# 正式アイドルリソース受入れ要件

## 1. 目的

既存のdiagnostic runではなく、製品完成ゲートである50 MiB未満、CPU平均0.5%以下を正式なacceptance evidenceとして確定する。

## 2. VMと物理機の役割

Hyper-V VMではスクリプトとfailure pathを診断する。

正式なリソース閾値は仮想化オーバーヘッドの影響を避けるため、Windows 11 x64物理release-hardwareで実行する。

このテストはファイルを破壊しないため、ユーザーのWindows 11 PCで実行可能である。ただし実行前に大規模ビルド、ゲーム更新、同期処理等を止め、10分間の受入れ時間を確保する。

## 3. 測定条件

固定条件:

- Release build
- UI終了
- Agent起動
- Session Agent起動
- 通常の監視設定
- 測定時間600秒
- 最終300秒にbulk workloadなし
- threshold override禁止

測定値:

- Agent + Session Agent combined Private Working Set
- Working Set time series
- normalized CPU average
- process I/O write bytes
- queue depth
- process identity/start time
- Agent health
- sample count
- sample time span

## 4. Quiet witness

正式runはResourceMonitorだけで「静かだった」と自己申告しない。

別の軽量witnessが最終300秒の次をJSON化する。

- interval start/end
- bulk event count
- queue overrun count
- reconciliation active time
- benchmark/workload process不存在
- build/test process不存在

既存の`Test-ResourceBudgetAcceptance.ps1`が要求するquiet evidenceへ接続する。

## 5. 固定ゲート

- Combined Private Working Set peak/defined acceptance value: 50 MiB未満
- Average normalized CPU: 0.5%以下
- queue sampling欠落なし
- process identity変化なし
- measurement span 600秒を満たす
- quiet interval 300秒を満たす
- required counters存在
- `AcceptanceEligible=true`

ユーザーの明示承認なしにthresholdを緩める引数を使用しない。

## 6. ディスク書込み

「イベントなし時のディスク書込み原則なし」を数値artifactに残す。

5秒flushやhealth bookkeeping等の設計上必要な少量書込みがある場合、その原因、頻度、byte数を記録する。継続的に無意味な書込みを行っている場合はFAILとして修正する。

## 7. 完了条件

正式600秒runのartifactが現在のRelease buildで生成され、`AcceptanceEligible=true`、50 MiB/0.5%ゲートを両方通過すること。

過去runやdiagnostic runを流用しない。
