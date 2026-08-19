# Agent／Explorer実環境相関測定要件

## 1. 目的

ドライバーなしMVPで、実ファイルI/Oからプロセスをどの程度Exact/Correlated/Unknownとして特定できるか、Explorer clipboard copyの関連付けがどの程度成立するかを測定する。

この項目は「相関率を100%にする」要件ではない。記録上の品質分類が正しく、失敗をUnknownまたはNew Createとして扱うことを確認する。

## 2. 自動Agent相関workload

Hyper-V Windows 11 VMで `StorageChronicle.FileMutationWorkload` を複数プロセスとして起動する。

最低ケース:

- 単一process create/write/rename/delete
- 親process→子process
- 2 process同一directory交互変更
- 2 process別directory並列変更
- burst
- process終了直前変更
- PID reuseを模擬できる連続process生成
- directory tree作成
- move
- metadata change

workloadは自身のPID、start time、exe path、scenario IDをoracleへ保存する。

SC側のProcessInstance/qualityと比較してExact/Correlated/Unknown率を出す。

## 3. Explorer相関

Explorerの実processを測るため、SC内部のfake flagや疑似`explorer.exe` process名で代用しない。

自動化可能な範囲ではWindows UI Automation/clipboard APIで専用Explorer windowを操作してよい。ただし不安定なUI automationを成功条件の唯一の根拠にしない。

最終matrixには最低限次の実Explorer操作を含む。

- copy 1 file → paste
- copy directory tree → paste
- same clipboard generationで2回paste
- clipboard内容変更後paste
- rename
- same-volume move
- drag-and-drop copy
- drag-and-drop move
- delete
- recycle bin move/restoreが安定して測れる場合

Explorer copy元相関は仕様通り次へ分類する。

- ConfirmedIntent
- CorrelatedSource
- SourceUnknown
- NotIdentified

copyと特定できない場合は新規作成扱いを正答とする。

## 4. 人間補助の最小化

UI AutomationでExplorer copy/pasteが安定して再現できる場合は全自動にする。

自動化がflakyなら、最終Explorer acceptanceだけ人間補助モードに切り替える。スクリプトが専用TestLabフォルダーと操作一覧を準備し、ユーザーはVM内Explorerで番号順に操作するだけとする。

人間補助は最大20操作以内にまとめる。

## 5. 計測結果

`artifacts/acceptance/correlation/<run-id>/` に保存する。

- workload oracle
- process correlation rows
- Exact count/rate
- Correlated count/rate
- Unknown count/rate
- Explorer scenario rows
- copy intent count
- source correlated count
- source unknown count
- not identified count
- false attribution count
- file/state correctness count
- environment

最重要の失敗は「誤ったprocessをExactとして記録」または「推測をConfirmedとして記録」することである。

Unknownが多いだけなら結果を正直に残し、製品要件違反とは限らない。

## 6. UI

Correlated processは通常UIでプロセス名を表示してよい。詳細ではquality=Correlatedを確認できること。

Source記録ではExactとCorrelatedを混同しない。

## 7. 完了条件

実I/Oと実Explorer操作を使った測定artifactが生成され、false Exact attributionが0で、相関不能ケースが仕様通りUnknown/New Createへ落ちること。
