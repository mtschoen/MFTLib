### Task B4: Port index watch tests: checkpoint loss and isolation

**Files:** Create `MFTLib.Tests/Index/FileIndexCacheOnlyUnresumableWatchTests.cs`, `FileIndexMidSessionCheckpointLossTests.cs`, `ConsumerJournalIsolationTests.cs`, and `FileIndexCheckpointLossDetectionTests.cs` if B1 deleted it, from their base-commit versions. `ConsumerJournalIsolationTests` must still prove that `JournalIsolation.OverrideJournalWindow` drives a watch-fault classification; the callback may now run on any drive's pump thread concurrently.

- [ ] Port; add `WatchFaultOnT_LiveWatchLossRecordedBeforeWatchFaultedRaised` if the ported set does not already pin it; commit.

**Gate:** green. **Depends on:** B1.

