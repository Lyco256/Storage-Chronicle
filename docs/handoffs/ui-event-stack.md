# feat/ui-event-stack handoff

## 変更範囲

- `src/StorageChronicle.UI.EventStack/**` に、Avalonia/MVVMのCompiled Binding実UI、ViewModel、UI専用Projection query、詳細/フィルター/ページ/Live/キーボード状態、Material Icons resolverを追加。
- `tests/StorageChronicle.UI.EventStack.Tests/**` にFake ProjectionによるVMテストとAvalonia Headless実UIテストを追加。
- `docs/src/StorageChronicle.UI.EventStack/**` の説明を実装へ同期。
- `docs/handoffs/ui-event-stack.md`を追加。

Storage、Windows API、共有契約、Requirements、solution外部ファイルは変更していない。

## 要件対応

初期Grouped・新しい順、Source/Normalized/Grouped即時切替、操作グループの子展開、昇降順、50/100/250/500行ページサイズ、メール一覧型ページ移動、グループ子の同一ページ境界、Live追従停止/再開、検索/品質/Origin/Operation filter、保存filter、詳細品質・Origin・時刻/offset/Sequence、親子Process移動、Reconciliation/UnverifiedGap/ExistenceOnly表示、tooltip/AutomationProperties/keyboard、Material Icons意味IDを実装した。

`IEventStackProjection`だけに依存し、Fake Projectionで動く。Page取得はasyncで、10万件fixtureでも要求ページだけをViewModel化する。

## 検証

- `dotnet restore tests/StorageChronicle.UI.EventStack.Tests/StorageChronicle.UI.EventStack.Tests.csproj` — 成功。
- `dotnet build tests/StorageChronicle.UI.EventStack.Tests/StorageChronicle.UI.EventStack.Tests.csproj --no-restore` — 成功、警告0。
- `dotnet test tests/StorageChronicle.UI.EventStack.Tests/StorageChronicle.UI.EventStack.Tests.csproj --no-build --no-restore` — 8件成功（Headless実UIを含む）。

## 既知の制限

Projectionからの保存済みfilter永続化はこのUI境界では行わず、画面内の保存filterとして扱う。永続化が必要な場合は上位Settings/Projection統合で別途接続する。

## 共有契約・競合

共有契約、Storage、Windows APIは参照・変更していない。トップCodexはこのブランチをレビュー後、`devenv`へno-ff統合すること。
