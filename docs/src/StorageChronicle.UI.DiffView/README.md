# UI Diff View

`StorageChronicle.UI.DiffView` は、共通 File System Projection を Tree/Explorer の二つの Headless renderer に分配する UI 層である。Tree は path ancestor と descendant を expansion 時だけ materialize し、Explorer は bounded page を返す。

表示には8 Explorer mode、primary/sub gutter icon と color token、仮想削除/不明場所 root、Explorer open 可否、path navigation、split panes、Live pause、Replay timeline/cursor/speed、Move reciprocal navigation が含まれる。OS のファイル API は `IExplorerLauncher` の外部 adapter に限定される。
