# 確認済み再調整の実処理要件

## 1. 現在の不足

`docs/release/main-readiness.md` に記録されている通り、連続性欠落のUI確認と拒否経路は存在するが、ユーザーが実行を選んだ経路が監視再開だけで終わっている。

この項目では、選択volumeに対する実際のreconciliationを実装する。

## 2. 記録規則

再調整で発見した差分はLiveObservedやJournalRecoveredとして偽装しない。

最低限次を記録する。

- source/origin = reconciliation
- scan run ID
- observed_at = 再調整で発見した時刻
- uncertain_from = 最後に連続性が保証された境界
- uncertain_to = observed_at
- process = unknown
- time quality = uncertain
- 差分の種類
- 取得できた旧状態と新状態
- metadata quality

ファイル自身のLastWriteTimeを実操作時刻として扱わない。

## 3. 実行対象

確認ダイアログでユーザーが「実行する」を選択したvolumeだけを対象にする。

手動再調整メニューは追加しない。

「実行しない」を選択したgapは`UnverifiedGap`として確定し、同じgap IDについて再度確認しない。

## 4. NTFS

NTFSでは次を行う。

1. 再調整開始境界を確定する。
2. 公開FSCTL APIによるMFT列挙を行う。
3. 保存済みFile ID、Parent ID、Name、Last USN等と比較する。
4. 変更候補だけ抽出する。
5. `24_NTFS_CANDIDATE_METADATA_PRIVILEGE_LOW_IO.md` に従い候補だけ詳細メタデータを取得する。
6. 差分イベントを追記ログへdurable appendする。
7. durable append成功後に状態DBを更新する。
8. 再調整中に到着した通常イベントを境界で重複排除して適用する。
9. 完了境界と品質を保存する。

再調整で子孫人工イベントを生成しない。

## 5. 非NTFS

非NTFSまたは永続journalなしでは、Directory Snapshot Readerで現在ツリーを列挙し、保存状態との差分を計算する。

- 内容を読まない。
- 内容ハッシュを計算しない。
- 再調整中に受けた`ReadDirectoryChangesW`イベントを捨てない。
- 初期scan境界より後の通知をscan結果へ重ねる。
- 通知欠落が再度起きたら新しいgapとしてfail-closedする。

## 6. 同時更新

再調整中にファイルが変わることを正常ケースとして扱う。

単一巨大lockでvolume監視を停止してはならない。

再調整開始前の境界、scan、scan中のliveイベント、完了境界を明確に分け、最終状態が最新liveイベントまで進むようにする。

## 7. 失敗

途中キャンセル、access denied、volume取り外し、VHDX detach、Agent停止、ストレージ書込み失敗の場合、部分結果を「完了済みreconciliation」にしない。

既にdurable appendしたSource/Canonical履歴は削除しない。

再調整RunをFailedまたはInterruptedとして残し、新しいgapを作る。

## 8. TestLab受入れ

`SC_TEST_VOLUME` と `SC_TEST_NONNTFS_VOLUME` で実行する。

最低ケース:

- NTFS: 保存状態からCreate/Rename/Move/Delete/MetadataChangeを発生させ、reconciliation runnerが差分を発見する。
- NTFS: 変更なしなら差分0。
- NTFS: scan中に追加変更し、重複または欠落しない。
- 非NTFS: 通知停止中の変更をsnapshot差分で発見する。
- ユーザー拒否: scanを一切開始せずUnverifiedGapを残す。
- 途中detach: Completedにしない。

実環境scanを行うが、TestLab VHDX以外を対象にしない。

## 9. 完了条件

確認ダイアログの「実行する」から、実volume scan、候補比較、durable差分保存、state更新までが接続されること。

再調整後のPoint-in-Time、Period Diff、Event Stackで`ReconciliationDiscovered`相当の事実を確認できること。
