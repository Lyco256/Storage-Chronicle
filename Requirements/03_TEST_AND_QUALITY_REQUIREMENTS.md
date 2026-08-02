# テスト・品質要件

## 1. テスト基盤

- xUnit v3を全テストの標準にする。
- UIはAvalonia.Headless.XUnitを使う。
- アーキテクチャはArchUnitNET xUnit v3を使う。
- 性能測定はBenchmarkDotNetを使う。
- コードカバレッジはMicrosoft Testing PlatformのCodeCoverage拡張を使う。
- テストは再現可能で、時計、乱数、ファイルシステム障害を注入できる設計にする。

## 2. テスト区分

### Unit

- 値型と不変条件。
- Event正規化と読取りイベント非永続化。
- 状態遷移。
- 時点パス再構成。
- フォルダー移動・削除の子孫計算。
- Grouped集約。
- Diff優先順位。
- フィルター。
- セグメントCodec。
- チェックサム。
- マニフェスト分岐。

### Contract

同じインターフェースの全実装に共通テストを適用する。

- `IEventStore`
- `IStateStore`
- `ISourceEventCollector`
- `IVolumeSnapshotReader`
- `IProjectionService`
- IPCシリアライズ。

### Integration

- SQLite実ファイル。
- 追記ログとSQLiteの整合。
- 破損セグメントを飛ばして他セグメントを読む。
- SQLite削除後の再構築。
- Agentパイプライン。
- Named Pipe接続切断。
- 媒体ミラー取込み。
- UIとFake Agent。

### Windows privileged

既定`dotnet test`から分離し、カテゴリを付ける。

- USN照会。
- USN読取り。
- MFT高速列挙。
- テストスクリプトが作成・マウント・破棄する専用NTFS VHDX。通常システムドライブを破壊的テストに使わない。
- ETW。
- SMB共有スナップショット。
- Windows Service起動停止。
- 外付け媒体接続相当。
- ReadDirectoryChangesW通知と欠落。

### UI Headless

- Event Stackの3モード。
- Grouped展開。
- ページング。
- 自動追従停止・再開。
- Diff ViewのTree。
- Explorer各表示モード。
- アイコンガター整列。
- 分割枠の追加・消滅・固定。
- 全数照合ダイアログ。
- 削除済み仮想フォルダー移動。

### End-to-End

合成Source EventをAgentへ投入し、保存、状態更新、IPC、UI Projectionまで検証する。OS依存の不安定な時刻待ちを避け、仮想時計を使う。

## 3. 必須障害テスト

- 書込み途中でプロセス終了。
- 最後のセグメント末尾切断。
- チェックサム不一致。
- SQLite破損または削除。
- ディスク容量不足を模擬。
- アクセス拒否。
- ファイルが問い合わせ中に消える。
- 外付け媒体が書込み中に外れる。
- Journal ID変化。
- USN範囲切詰め。
- 通知バッファー欠落報告。
- Agent再起動。
- Session Agent再接続。
- IPCバージョン不一致。
- PC時刻逆行。
- 同一PID再利用。
- 同名別File ID。
- フォルダー移動後の子孫追加・削除。
- 媒体履歴分岐。
- 媒体ログフォルダー削除。

## 4. 正確性テスト

次をUTF-8 JSONのGolden Fixtureへ固定する。USN等の生バッファーだけは別のバイナリfixtureとする。

- ファイル作成から削除。
- 短命ExistenceOnly。
- 同一パス実体入替え。
- フォルダー移動と子孫パス。
- フォルダー削除と削除前子孫。
- 期間内作成・削除。
- 複数名前変更。
- 移動後DataWrite。
- Explorerコピー候補相関。
- Recycle/Restore分類。
- 全数照合差分。
- 不明プロセス。
- Share変更。
- OneDriveプレースホルダー状態。
- Mount Sessionと別PC取込み。

Golden Fixtureの期待結果変更はトップCodexレビューを必須にする。

## 5. 性能テスト

BenchmarkDotNetで最低限次を測る。

- 100万File IDのMFT列挙結果取込み。
- 100万ノード状態からの単一点パス再構成。
- 大規模フォルダー移動の記録。
- 10万イベントのGrouped生成。
- 10万行Event Stackページ取得。
- 大規模Period Diff。
- 追記セグメント書込み。
- Zstandard圧縮。
- SQLite索引投入。
- 媒体マニフェスト統合。

性能テストは正確性テストと分ける。ベンチマーク閾値を満たすためにイベントを削除しない。

## 6. メモリ・CPU受け入れ測定

専用のプロセス測定ハーネスを用意する。

条件：

- UI終了。
- AgentとSession Agent稼働。
- システムドライブ監視。
- 起動後10分。
- 最後の5分で大量イベントなし。

測定：

- Private Working Set合計。
- Working Set推移。
- CPU平均。
- ディスク書込み量。
- キュー長。

目標：

- Private Working Set合計50MiB未満。
- CPU平均0.5%以下。

この二値は完成ゲートであり、未達のまま測定結果だけを残して完了としてはならない。ユーザーが具体的な測定結果を確認して例外を明示承認した場合だけmain昇格を許可する。
- イベントなし時のディスク書込み原則なし。

未達時も機能または記録品質を削除せず、結果と原因を文書化して最適化を継続する。

## 7. カバレッジ

行カバレッジだけを品質判定にしない。最低ゲート：

- Domain、State、Projection、Storage Codec：80%以上。
- Windows P/Invoke薄層：カバレッジ率をゲートにせず、ラッパー上の契約テストを必須。
- UI ViewModel：70%以上。
- 重要不変条件、破損復旧、ログ再構築は100%の分岐ケースをテスト一覧で明示する。

## 8. 静的品質

- 警告ゼロ。
- Nullable警告ゼロ。
- 公開API XMLコメント。
- P/Invoke構造体のサイズとLayoutテスト。
- `unsafe`はWindows相互運用の限定ファイルだけ。
- `unsafe`使用ファイルに理由、境界検証、テストを文書化。
- UIスレッドで同期I/Oを行わない。
- `async void`はイベントハンドラ以外禁止。
- fire-and-forgetタスクは中央Supervisorへ登録する。
- CancellationTokenを長時間処理に必須とする。

## 9. テストコマンド

トップCodexは`build`配下に次を実装する。

- `build/Build.ps1`
- `build/Test-Fast.ps1`
- `build/Test-All.ps1`
- `build/Test-WindowsPrivileged.ps1`
- `build/Test-Ui.ps1`
- `build/Benchmark.ps1`
- Linux向け同等`.sh`

上記ルートスクリプトはトップCodex所有とし、品質エージェントは`build/quality/**`、インストーラーエージェントは`build/package/**`だけを編集する。

スクリプトは失敗コードを正しく返し、結果を`artifacts/`へ出す。`artifacts/`はGit管理外。
