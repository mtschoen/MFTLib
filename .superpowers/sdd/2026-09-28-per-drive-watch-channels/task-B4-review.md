### Plan Compliance
- Plan compliant, with two Minor gaps below. Test-only change; three files created; `FileIndexCheckpointLossDetectionTests` correctly left alone (B1 already ported it).
- Cannot verify from diff: (a) that the pinned start-refusal contract matches the spec: `StartWatchingAsync(X)` on an unresumable drive throws `InvalidOperationException` naming `RescanAsync`, requests no watch, `StopWatchingAsync(X)` then throws, `RescanAsync(X)` does not start the drive (FileIndexCacheOnlyUnresumableWatchTests.cs, `RefuseStartAsync` and the rescan/only-drive tests; ConsumerJournalIsolationTests.cs `LostWindow_...` cacheOnly branch). The implementer derived these by observation; the controller should check them against the spec text. (b) The report's whole-suite run (1598 tests) and the whole-tree `aislop scan .` (only `--staged` was run; gap for the controller to close).

### Port accounting (base c1d43784 vs 0f7e292)
FileIndexCacheOnlyUnresumableWatchTests (5 base, 4 new):
- Renamed and declared (3 of 4): LeavesTheUnresumableDriveOut... -> RefusesTheUnresumableDriveAndWatchesTheHealthyOne; OnTheUnresumableDriveWhileWatching_ClearsTheRefusalAndArmsIt... -> OnTheUnresumableDrive_ClearsTheRefusalAndLeavesTheDriveReadyToStart; ..._WhenTheScanFailsWithoutThrowing_... -> same without "WhileWatching".  Fourth rename: WhenEveryMftDriveIsUnresumable_StartsNoSession... -> WhenTheOnlyMftDriveIsUnresumable_RefusesItAndReportsIt. All four renames are declared and each old name states something no longer true (session, arms onto running watch).
- Dropped and declared (1): RescanAsync_StartedBeforeAnyWatchSession_StillArms... (see Minor 1).
FileIndexMidSessionCheckpointLossTests (9 base, 9 new):
- Unchanged names (7): TrimmedCheckpoint, AfterAppliedBatches, RecreatedJournal, StillInTheJournal, VolumeThatCannotAnswer, OneDriveLosesItsPosition, ASuccessfulRescanClearsAMidSessionLoss.
- Renamed and declared: SourceEndingWithoutAStop_ClassifiesEveryWatchedDrive -> WatchEndingWithoutAStop_ClassifiesEachDriveAgainstItsOwnJournal.
- Dropped and declared: WatchFaultOnADriveWithNoBlock_QueriesNoJournal. Reason holds: per-drive watches start only over an MFT block, and a fault is raised by a drive's own pump, so a fault for a block-less letter is unreachable.
- Added: WatchFaultOnT_LiveWatchLossRecordedBeforeWatchFaultedRaised (brief-named; report shows a scratch mutation failing it).
ConsumerJournalIsolationTests: 1 [TestMethod] + 3 [DataTestMethod] (10 rows) in base and new, all names unchanged. Textual diff vs base is only source-API adaptation. No base method is unaccounted for.

### Strengths
- Every base method is ported, renamed-and-declared, or dropped-and-declared; the commit message list matches the mechanical listing exactly.
- ConsumerJournalIsolationTests keeps class-level `[DoNotParallelize]` (line 17), never nests scopes (one `using var scope` per test; the exceptional-exit test closes its first scope before opening the second), and disposes indexes before scopes (`await using var index` is declared after `using var scope`, so it disposes first; the watch-fault test's stop completes before disposal). It still proves the override drives classification: `WatchFault_UsesLatestSyntheticWindowAndAppliedCursor` asserts `LiveWatch` detection, cause, and the applied cursor 2_000 for windows 1 and 2, and null for the retained window 0.
- Override callbacks: the isolation tests use `ConcurrentDictionary` (reads and writes across threads); the mid-session tests use stateless lambdas and a `ConcurrentQueue` for the recording one (MidSession ...:StillInTheJournal); `Journals(Dictionary)` in the unresumable tests is never mutated after construction. No unsynchronized mutable state.
- No handler blocks on a lifecycle call: the `WatchFaulted` handlers only read `Drives` and complete a TaskCompletionSource created with `RunContinuationsAsynchronously`.
- Awaits are bounded: fault waits go through `WatchHarness.WaitForFaultAsync` (WaitAsync with HangGuard when pending), the isolation fault wait uses `WaitAsync(HangGuard)`, and the rest use the test-context token as in the sibling tests. No sleeps, no elapsed-time assertions.
- The new test genuinely pins ordering (the handler reads the loss), and the fault-kind filter avoids a false pass from another fault.
- Assertions were not weakened beyond contract changes; the new `Assert.AreEqual(refusal.Message, status.WatchFailureMessage)` and start-count assertions are at least as strict.

### Issues
#### Critical (Must Fix)
None.

#### Important (Should Fix)
None.

#### Minor (Nice to Have)
1. Dropped session test has an uncovered per-drive analogue. FileIndexCacheOnlyUnresumableWatchTests.cs (missing test). The dropped case pinned "a drive that only became watchable mid-rescan is not left off". Its per-drive form is a `StartWatchingAsync('T')` issued while T's rescan runs over an UNRESUMABLE block: the start must wait on the lifecycle gate and then judge resumability against the fresh block, not refuse on the stale one. The report says `Rescan_HoldsLifecycleGateThroughProduction` covers it, but that test (FileIndexPerDriveWatchTests.Lifecycle.cs:182-197) uses a healthy harness block, so a refusal decided before taking the gate would pass it. `HoldNextProduction` exists; a short test in this file would close it. Suggest adding it or confirming a later task does.
2. `StopWatchingAsync` assertions now only check the wrapper type `DriveWatchFaultException` (MidSession lines in every test, e.g. `WatchFaultWhileTheWatchPositionIsStillInTheJournal_LeavesTheLossNull`; Isolation `WatchFault_Uses...` last line). The base pinned the original exception type (IOException, UnauthorizedAccessException). If the wrapper carries the original as `InnerException`, assert it at least once (the UnauthorizedAccessException case) so the fault's cause is still proven to surface.
3. `RescanAsync_OnTheUnresumableDrive_WhenTheScanFailsWithoutThrowing_LeavesTheRefusalIntact` (Unresumable file, the `await index.RescanAsync('T', Token)` line): the test silently pins "rescan returns normally when the producer yields no block" and its doc comment does not say so, even though the implementer suspects the spec requires an `InvalidOperationException`. Add one comment line saying the test pins the current return-normally behavior, so the later fix changes it deliberately. The doc comment also keeps the base's "Review finding 1 (PR 230, round 1)" history note; the plan says describe target state only.
4. `WatchFaultWhileTheWatchPositionIsStillInTheJournal_LeavesTheLossNull` changed `ArmedUsn - 500` to `ArmedUsn - 50` (MidSession, override lambda). Justified (harness cursor is 100, so -500 would be negative) and still strictly behind the position, but a one-line comment would stop a reader from thinking it was tuned.

### Assessment
Task quality: Approved
Reasoning: The port accounting is complete and correct, no assertion is weakened beyond what the per-drive contract forces, the isolation, thread-safety and no-blocking-handler requirements all hold, and the added ordering test is real. Only Minor polish and one small coverage gap for the dropped rescan-versus-start case remain; the aislop gap is `--staged` only.
