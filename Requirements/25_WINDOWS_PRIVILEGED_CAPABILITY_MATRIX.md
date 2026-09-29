# Windows特権機能受入れマトリクス要件

## 1. 目的

現在`NOT_EXECUTED`になっているWindows特権機能を、`37_PHYSICAL_READ_ONLY_ACCEPTANCE.md` の監査済み実機隔離領域で実API・実I/Oとして実行する。

Fake Collectorだけの成功でこの項目を閉じない。

## 2. 必須マトリクス

Windows 11実機で最低限次を実行する。製品の観測は既存データに対して読み取り専用とし、変更系の操作はテスト用workloadがrun専用fixtureまたは新規file-backed VHDX内だけで行う。

| Capability | 必須確認 |
|---|---|
| VHDX | create, attach, initialize, format, detach, destroy |
| USN query | query journal identity/range |
| USN read | FileMutationWorkloadの実変更を回収 |
| MFT | public FSCTL enumeration |
| Reconciliation | 実VHDXの差分検出 |
| ETW | session start/stop, file/process correlation |
| ReadDirectoryChangesW | 実create/rename/delete通知 |
| Buffer gap | bounded testでgapを検出しfail-closed |
| SMB share | 専用物理テストPC上のrun専用ディレクトリ・共有名だけをcreate/change/remove。既存共有設定へ触れない |
| Service | 既存製品・履歴のない専用物理テストPCでinstall/start/stop/recovery config |
| Session Agent | interactive user session接続 |
| Clipboard | CF_HDROP/clipboard generation取得 |
| Volume GUID | drive letterなしreadable volume識別 |
| Hot attach/detach | 外付け媒体接続相当 |
| ACL denied metadata | SeBackupPrivilege path |
| Non-NTFS | best-effort notification + reconciliation |

実物理USBはこのソフトウェア受入れマトリクスの必須条件にしない。実機上でfile-backed VHDXをmount/dismountして接続相当を検証する。実USBはユーザーが専用テスト媒体を提供した場合だけ任意で測定する。

## 3. 実I/O oracle

`StorageChronicle.FileMutationWorkload` が生成したoracleと、SCのSource Event、Canonical Event、最終Stateを比較する。

全操作が一対一イベントになることは要求しない。製品仕様でまとめられるイベントはCanonical/State結果で判定する。

最低判定:

- createが存在として残る
- writeがDataWrite系として残る
- rename/moveがFile ID同一性を維持する
- delete後も削除前metadataを参照できる
- directory move/deleteが子孫人工イベントを生成しない
- process qualityがExact/Correlated/Unknownのいずれかで記録される
- source factsとUI inferenceが混同されない

## 4. データ破壊防止

このマトリクスの変更操作は37の隔離境界と、run GUID・所有markerが一致する新規file-backed VHDXだけに限定する。

`\\.\C:`、system/boot/回復volume、既存の物理disk、既存ユーザーデータが指定された場合はテストをskipではなくfail-closedで拒否する。

既存のMFT benchmarkやWindows privileged scriptに物理C:を例示・許可する経路がある場合、安全guardを追加してrun専用VHDXからmountしたvolume以外をacceptance modeで拒否する。

## 5. 成果物

`artifacts/acceptance/windows-privileged/<run-id>/` に最低限次を保存する。

- environment.json
- capabilities.json
- oracle.json
- source-event-summary.json
- canonical-summary.json
- final-state-summary.json
- reconciliation-summary.json
- service-summary.json
- errors.json
- result.json

各capabilityを `PASS`, `FAIL`, `NOT_SUPPORTED`, `NOT_EXECUTED` で明示する。

必須項目に`NOT_EXECUTED`が1つでもあればAcceptanceEligible=falseとする。

## 6. 完了条件

Windows 11実機で全必須capabilityが実行され、製品仕様で未対応が許されている能力以外はPASSし、承認済みfixtureと製品専用保存先以外の既存データを一切変更しない。
