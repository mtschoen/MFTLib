### Task B2: Port index watch tests: watch, pump, faults, catch-up

**Files:** Create `MFTLib.Tests/Index/FileIndexWatchTests.cs`, `FileIndexWatchPumpTests.cs`, `FileIndexWatchFaultTests.cs`, `FileIndexWatchCatchUpTests.cs`, `FileIndexWatchCatchUpLinkedWaitTests.cs` from their base-commit versions (`git show c1d43784:MFTLib.Tests/Index/<file>`). `FileIndexWatchCatchUpRetentionTests.cs` is not ported: its subject (`CatchUpCoordinator` collection, `_catchUpCoordinatorCreatedForTest`) is gone; B7 covers the batched wait.

Port every case that still describes the per-drive contract onto `FakeIndexWatchSource` and `WatchHarness`: a `DriveWatchFailure` item becomes `FailDrive`; `WatchFaultKind.Source` with a drive becomes `Drive` or `Channel` by what the case exercises; a null-drive case is dropped. Session, readiness, reclaim and ledger cases are dropped. The commit message lists every dropped method and why.

- [ ] Port; run each file's tests; whole suite; `aislop scan .`; commit "Port FileIndex watch, pump, fault and catch-up tests to per-drive watches".

**Gate:** green. **Depends on:** B1. **Parallel with:** B3, B4, C2, C3a, C3b.

