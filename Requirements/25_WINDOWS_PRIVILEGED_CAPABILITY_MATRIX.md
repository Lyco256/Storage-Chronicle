# Windows特権機能受入れマトリクス要件

## 1. 目的

現在`NOT_EXECUTED`になっているWindows特権機能を、`22_SAFE_HYPERV_TESTLAB.md` の隔離環境で実API・実I/Oとして実行する。

Fake Collectorだけの成功でこの項目を閉じない。

## 2. 必須マトリクス

Windows 11 TestLabで最低限次を実行する。

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
| SMB share | TestLab内ディレクトリをshare/create/change/remove |
| Service | install/start/stop/recovery config |
| Session Agent | interactive user session接続 |
| Clipboard | CF_HDROP/clipboard generation取得 |
| Volume GUID | drive letterなしreadable volume識別 |
| Hot attach/detach | 外付け媒体接続相当 |
| ACL denied metadata | SeBackupPrivilege path |
| Non-NTFS | best-effort notification + reconciliation |

実物理USBはこのソフトウェア受入れマトリクスの必須条件にしない。Hot-add/hot-remove VHDXを外付け媒体接続相当として使う。実USBは将来の任意hardware portability testとする。

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

このマトリクスの破壊操作はTestLab marker付きVHDXだけに限定する。

`\\.\C:`、VM OS volume、ホストvolume、実ユーザーデータが指定された場合はテストをskipではなくfail-closedで拒否する。

既存のMFT benchmarkやWindows privileged scriptにC:を例示・許可する経路がある場合、安全guardを追加して専用TestLab volume以外をacceptance modeで拒否する。

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

Windows 11 TestLabで全必須capabilityが実行され、製品仕様で未対応が許されている能力以外はPASSし、TestLab外のvolumeを一切変更しない。
