# UsnJournalReader.cs

`UsnJournalReader` performs Journal ID and USN continuity checks before streaming bounded `FSCTL_READ_USN_JOURNAL` batches. It reads only after the persisted USN, stops at the current journal continuation, and stores the latest Journal ID/next USN as `LastObservedState` for the agent. Journal creation, capacity changes, and whole-volume rescans are outside this class.

If the journal is absent, the ID changes, or the persisted USN is below the valid range, the reader emits an explicit gap and stops that pass for recovery. Access denial and media removal become explicit gap results; cancellation is rethrown and prevents the next native read. `UsnReadOptions` bounds buffer size, timeout, and reason mask.
