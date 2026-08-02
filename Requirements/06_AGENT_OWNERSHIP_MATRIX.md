# サブエージェント所有パス一覧

この表はトップCodexがworktree作成前に確認する。共有ファイルへ複数エージェントを割り当てない。

| Branch | Wave | Product paths | Test paths |
|---|---:|---|---|
| `feat/normalization` | 1 | `src/StorageChronicle.Normalization/**` | `tests/StorageChronicle.Normalization.Tests/**` |
| `feat/windows-filesystem` | 1 | `src/StorageChronicle.Platform.Windows.FileSystem/**` | `tests/StorageChronicle.Platform.Windows.FileSystem.Tests/**` |
| `feat/settings` | 1 | `src/StorageChronicle.Settings/**`, `src/StorageChronicle.UI.Settings/**` | `tests/StorageChronicle.Settings.Tests/**`, `tests/StorageChronicle.UI.Settings.Tests/**` |
| `feat/state-engine` | 1 | `src/StorageChronicle.State/**` | `tests/StorageChronicle.State.Tests/**` |
| `feat/projection-grouping` | 1 | `src/StorageChronicle.Projection/**` | `tests/StorageChronicle.Projection.Tests/**` |
| `feat/storage-engine` | 1 | `src/StorageChronicle.Storage/**` | `tests/StorageChronicle.Storage.Tests/**` |
| `feat/windows-ntfs` | 1 | `src/StorageChronicle.Platform.Windows.Ntfs/**` | `tests/StorageChronicle.Platform.Windows.Ntfs.Tests/**` |
| `feat/windows-session-share` | 1 | `src/StorageChronicle.Platform.Windows.Session/**`, `src/StorageChronicle.SessionAgent/**` | `tests/StorageChronicle.Platform.Windows.Session.Tests/**` |
| `feat/ui-event-stack` | 1 | `src/StorageChronicle.UI.EventStack/**` | `tests/StorageChronicle.UI.EventStack.Tests/**` |
| `feat/ui-diff-view` | 1 | `src/StorageChronicle.UI.DiffView/**` | `tests/StorageChronicle.UI.DiffView.Tests/**` |
| `feat/agent-ipc` | 2 | `src/StorageChronicle.Application/**`, `src/StorageChronicle.Agent/**`, `src/StorageChronicle.Contracts/Runtime/**` | `tests/StorageChronicle.Agent.Tests/**`, `tests/StorageChronicle.Integration.Tests/Agent/**` |
| `feat/external-media` | 2 | `src/StorageChronicle.ExternalMedia/**` | `tests/StorageChronicle.ExternalMedia.Tests/**` |
| `feat/integration-quality` | 3 | `build/quality/**`, `tools/StorageChronicle.TestDataGenerator/**`, `tools/StorageChronicle.LogInspector/**`, `benchmarks/StorageChronicle.Benchmarks/**` | `tests/StorageChronicle.Architecture.Tests/**`, `tests/StorageChronicle.Integration.Tests/CrossModule/**`, `tests/StorageChronicle.WindowsIntegration.Tests/**`, `tests/StorageChronicle.UI.Headless.Tests/**`, `tests/StorageChronicle.EndToEnd.Tests/**` |
| `feat/installer-packaging` | 3 | `installer/**`, `build/package/**` | `tests/StorageChronicle.Installer.Tests/**` |

## トップCodex専有

- `src/StorageChronicle.Domain/Contracts/**`
- `src/StorageChronicle.Contracts/**`のうち`Runtime/**`を除く契約定義
- `src/StorageChronicle.Platform.Abstractions/**`
- `src/StorageChronicle.UI.Shared/**`
- `src/StorageChronicle.UI.Desktop/**`
- `tests/StorageChronicle.Domain.Tests/**`
- `tools/StorageChronicle.DocMirrorValidator/**`
- `tests/StorageChronicle.DocMirrorValidator.Tests/**`
- ルートのsolution、Directory、global、AGENTS、README、Git設定
- `Requirements/**`
- `build/Build.ps1`、`build/Test-*.ps1`、`build/Benchmark.ps1`とLinux向け同等スクリプト
- `docs/architecture/**`、`docs/decisions/**`、`docs/release/**`
- `assets/**`
- merge後の複数モジュール修正

各サブエージェントは部分要件で明示された対応文書パスと固有ハンドオフ文書だけを編集する。
