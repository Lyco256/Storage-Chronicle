# Critical branch test matrix

This list is the explicit branch inventory required by `Requirements/03_TEST_AND_QUALITY_REQUIREMENTS.md`. Each row names a deterministic success, failure, corruption, cancellation, or recovery case; the tests must remain in the final gate.

| Invariant or branch | Test evidence |
|---|---|
| State create/rename/move/delete/restore and ancestor reconstruction | `tests/StorageChronicle.State.Tests/StateEngineTests.cs::CreateRenameMoveDeleteAndRestoreReconstructsPointInTimeState` |
| Folder move/delete does not synthesize descendants | `tests/StorageChronicle.State.Tests/StateEngineTests.cs::FolderMoveChangesOneEntryAndReconstructsOldAndNewPathsIncludingLaterChild` |
| State duplicate, gap, reverse sequence, cycle corruption, cancellation, and transactional no-mutation | `tests/StorageChronicle.State.Tests/StateEngineTests.cs::DuplicateIsIdempotentAndGapOrReverseLeavesStateReadyForRecovery`; `tests/StorageChronicle.State.Tests/StateEngineTests.cs::CancellationAndCycleCorruptionDoNotMutateState` |
| Append cancellation and capacity stop without deleting history | `tests/StorageChronicle.Storage.Tests/StorageEngineTests.cs::CancellationBeforeAppendLeavesNoRecord`; `tests/StorageChronicle.Storage.Tests/StorageEngineTests.cs::CapacityFailureStopsRecordingAndDoesNotDeleteHistory` |
| Corrupt segment isolation and SQLite rebuild | `tests/StorageChronicle.Storage.Tests/StorageEngineTests.cs::CorruptClosedSegmentIsSkippedWithoutBlockingHealthySegments`; `tests/StorageChronicle.Storage.Tests/StorageEngineTests.cs::DeletedSqliteIsRebuiltFromSegments` |
| Projection cancellation, unknown grouping, delete/move priority, and filters | `tests/StorageChronicle.Projection.Tests/ResilienceAndScaleTests.cs::CancellationIsHonoredBeforeProjectionWork`; `tests/StorageChronicle.Projection.Tests/DiffAndFilterTests.cs`; `tests/StorageChronicle.Projection.Tests/EventStackProjectionTests.cs` |
| External-media manifest/segment corruption, traversal, deduplication, branch, interruption recovery, and cancellation | `tests/StorageChronicle.ExternalMedia.Tests/ExternalMediaTests.cs` |
| Settings validation, Agent rejection, exception, success, restart state, and cancellation | `tests/StorageChronicle.UI.Settings.Tests/SettingsDialogViewModelTests.cs`; `tests/StorageChronicle.UI.Settings.Tests/SettingsDialogHeadlessTests.cs` |
| End-to-end restart and SQLite rebuild preserve canonical history | `tests/StorageChronicle.EndToEnd.Tests/GoldenFixtureTests.cs::CreationDeleteGoldenFixtureSurvivesAgentRestartAndSqliteRebuild` |

The 80%/70% coverage gate is separate from this matrix: line coverage cannot substitute for the named branch cases. Windows P/Invoke wrappers use capability-gated contract tests instead of a percentage threshold.
