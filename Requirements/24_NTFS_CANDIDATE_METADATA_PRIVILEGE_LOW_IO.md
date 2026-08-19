# NTFS候補限定メタデータ・SeBackupPrivilege・低優先I/O要件

## 1. 目的

NTFS reconciliationで全MFTエントリへ高コストな詳細問い合わせを行わず、変更候補だけ詳細メタデータを取得する。同時に、通常ACLで読めないメタデータを必要な範囲だけ`SeBackupPrivilege`で取得し、scan I/Oがユーザー操作を邪魔しないよう低優先化する。

## 2. 候補限定詳細取得

MFT lightweight enumeration段階では、候補判定に必要な構造情報だけを扱う。

候補判定後にだけ、既存production directory snapshot/metadata readerを使い次を取得する。

- logical size
- allocation size
- creation time
- last access time
- last write time
- filesystem change time
- file attributes
- reparse tag/state
- 必要なplaceholder state

ファイル内容を読まない。ファイル内容ハッシュを計算しない。

変更候補がN件なら詳細メタデータ問い合わせ回数が原則O(N)となること。全volume件数に比例した詳細問い合わせを行わない。

## 3. SeBackupPrivilege

`SeBackupPrivilege` はread-only metadata取得の補助にだけ使用する。

禁止:

- `SeRestorePrivilege`を有効化しない。
- privilegeをAgent生存期間中常時有効にしない。
- privilegeをUI、Session Agent、通常monitoring pathへ広げない。
- privilegeを使って書込み権限を回避しない。

推奨実装:

- reconciliation専用workerでthread impersonation tokenを用意する。
- `LookupPrivilegeValue` と `AdjustTokenPrivileges` で `SeBackupPrivilege` だけをscope内で有効化する。
- scope終了時にtoken/impersonationを必ず解除する。
- privilegeがtokenに存在しない、または有効化できない場合はscan全体をクラッシュさせず、通常ACLで取れる情報だけ保存しmetadata qualityを下げる。

ディレクトリhandleには必要な場合だけ`FILE_FLAG_BACKUP_SEMANTICS`を使う。

## 4. 低優先I/O

reconciliation workerはUI/通常live processingとは別のbackground workerで実行する。

- worker thread開始時に `THREAD_MODE_BACKGROUND_BEGIN` を使う。
- worker終了時はfinallyで `THREAD_MODE_BACKGROUND_END` を必ず実行する。
- metadata/file handleで利用可能な場合は `FileIoPriorityHintInfo` にLowまたはVeryLow/Idle相当を設定する。
- I/O priority hintがdriverに無視される、または設定失敗しても正確性を落とさず継続する。
- low priority化のためにイベントを間引かない。
- live event persistenceをreconciliationより常に優先する。

## 5. TestLab

ACL拒否シナリオを専用VHDXに作る。

1. 通常tokenでは対象ディレクトリの詳細問い合わせが拒否される。
2. `SeBackupPrivilege` scope内でread-only metadataを取得できる範囲を記録する。
3. scope終了後にprivilegeが通常pathへ残らない。
4. 対象内容を一切読み出していない。
5. read-only metadata pathが対象ファイルを変更していない。

低優先I/Oは、TestLabで同時にforeground I/O workloadを実行し、background mode設定の有無をログで確認する。性能値の改善を必須条件にはせず、OS優先度APIが正しく適用・解除されることを確認する。

## 6. 計測

reconciliation artifactに次を保存する。

- MFT entry count
- candidate count
- detailed metadata query count
- detailed query/candidate ratio
- privilege enable success/failure count
- ACL fallback count
- low-priority mode enable result
- I/O priority hint success/failure count
- elapsed time

## 7. 完了条件

候補0件のreconciliationで全volumeへの詳細問い合わせが発生しない。

候補限定query、scoped `SeBackupPrivilege`、background resource priorityが実コード経路で動作し、失敗時も記録品質を明示して安全に継続する。

## 8. 公式参照

- https://learn.microsoft.com/en-us/windows/win32/secauthz/privilege-constants
- https://learn.microsoft.com/en-us/windows/win32/secauthz/enabling-and-disabling-privileges-in-c--
- https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilea
- https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadpriority
- https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_io_priority_hint_info
- https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle
