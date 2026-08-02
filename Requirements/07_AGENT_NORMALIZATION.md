# サブエージェント要件：Normalization

## ブランチ

`feat/normalization`

## 所有パス

- `src/StorageChronicle.Normalization/**`
- `tests/StorageChronicle.Normalization.Tests/**`
- 対応する`docs/src/StorageChronicle.Normalization/**`
- `docs/handoffs/normalization.md`

## 目的

Source Eventを決定的なCanonical Eventへ変換し、取得元の事実、相関結果、品質を混同しない。

## 変換規則

- 作成、フォルダー作成、DataWrite、Extend、Truncate、基本情報変更、Security変更、Rename、Move、Delete、Recycle、Restore、Share、Cloud State、Reconciliation、GapをCanonical Operationへ変換する。
- Renameの旧名・新名はVolume、File ID、Source Sequenceを用いて上限付き状態で組み合わせる。片側しかない場合は欠損品質付き部分イベントとして残し、捏造しない。
- フォルダー移動・削除で子孫イベントを生成しない。関連操作IDと対象ルートだけをCanonical Eventへ付与する。
- `$Recycle.Bin`への出入りと同一File IDの親変更を使ってRecycleとRestoreを分類する。Recycle Bin補助ファイルの内容を読まない。確定不能時は通常Move/Deleteとして残す。
- ETWのProcess情報をSource Eventへ相関し、Exact、Correlated、Unknownを維持する。CorrelatedをExactへ昇格しない。
- Clipboard候補とExplorer作成系列は、同一Generation、copy意図、有効期間、相対ツリー、競合操作なしの全条件を満たす場合だけコピー元を関連付ける。フォルダー候補ではGeneration時点の状態Projectionから相対サブツリーを解決し、貼付け先の各作成項目へ同じGenerationと元項目参照を付与する。意図だけ確定ならCopySourceUnknown、判定不能ならCreate/DataWriteとする。
- cut意図は同一ボリュームのFile ID親変更が確認できた時だけMoveへ使用する。ドライブ間または対応不能なcutを推測Moveへ変換しない。
- Read、Open、Query、Directory Enumerationは永続Canonical Eventへ変換しない。相関に必要な間だけ上限付きメモリへ保持する。
- ShareとCloud StateをファイルMetadata Changeと混同しない。
- 同一Source Eventを再処理しても同じCanonical Event IDと結果を返す。

## テスト

USN理由組合せ、Rename片側欠損、Recycle/Restore、プロセス品質、Clipboard複数貼付け、フォルダーツリーコピー、cross-volume cut、競合コピー、読取り非永続化、Share、Cloud、Reconciliation、冪等性、時計逆行。

## 受け入れ条件

- 変換結果がOS非依存Canonical Eventである。
- 推測品質を失わない。
- 読取り系イベントが履歴容量を増やさない。
- ファイル内容または内容ハッシュを使わない。
