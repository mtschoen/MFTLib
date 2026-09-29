# Task B3 report: port index watch tests, rescan interplay

Worktree C:\Users\mtsch\MFTLib-worktrees\265-B3, branch task/265-B3, base df50af9 (verified equal to `rev-parse df50af9` in impl-265).
Commit: d594e98 "Port FileIndex rescan-while-watching tests".

## Implemented
Five test files created (all under MFTLib.Tests/Index), plus one added member in test support.
- FileIndexWatchRescanTests.cs (563 lines, size note at top): 16 methods incl. new Rescan_OfT_LeavesUsPumpRunning; `_swapGate` reflection kept in `SwapGateOf` (B5 replaces it); `_swapGate` verified to exist (FileIndex.cs:49).
- FileIndexWatchFailedRescanTests.cs: 4 methods (one data test, 2 rows).
- FileIndexWatchRescanFaultDuringProductionTests.cs: 1 method (re-cast, see concerns).
- FileIndexWatchRescanCheckpointLossTests.cs: 1 method.
- FileIndexWatchRecoveryFaultTests.cs: 5 methods (one data test, 2 rows).
- Test support addition (member only): `WatchHarness.CacheDirectory` (read-only property, returns the harness's temp cache directory), used by the swap-gate cancel test to assert no `*.retired-*` file. FakeIndexWatchSource untouched. Internal statics `ThrowsAsync`, `SwapGateOf`, `WaitForReplacementBlockAsync` live in FileIndexWatchRescanTests and are used by the other new files (no shared support file touched).
- Commit message lists every rename, dropped method and dropped data row (mechanical name diff in .superpowers/audit.txt of the worktree).

## TDD evidence
Ports are not test-first. New test Rescan_OfT_LeavesUsPumpRunning:
- The production scratch mutation (retire every other drive's watch inside RescanWithGatesHeldAsync) was DENIED by the auto-mode classifier ("Modify Shared Resources"), so I did not retry it via another route.
- Substitute, test-side only, reverted, never committed: inserted `await SwapGateOf(harness.Index).WaitAsync(Token)` after the production gate is entered, which reproduces the regression the test pins (an index-wide gate held across production). RED: `Failed Rescan_OfT_LeavesUsPumpRunning [20 s] System.TimeoutException` (U's Publish blocks on the held gate for the 10 s HangGuard, then the harness dispose times out too). Log: .superpowers/red1.log in the worktree.
- GREEN without the mutation: targeted run of the 5 new classes + FileIndexRescanCleanupTests + FileIndexPerDriveWatchTests: Passed 67, Failed 0.
The stronger production mutation (a rescan that stops other drives' watches) is not demonstrated; the orchestrator may run it if wanted.

## Verification
- Targeted: `dotnet test MFTLib.Tests ... --filter "FullyQualifiedName~FileIndexWatchRescanTests|...FailedRescanTests|...FaultDuringProductionTests|...CheckpointLossTests|...RecoveryFaultTests"`: Passed 30 (before the final duplicate removal); final run incl. cleanup and per-drive classes: Passed 67, Failed 0.
- Whole suite `scripts/run-coverage.ps1 -NonInteractive`: Total 1605, Passed 1599, Skipped 6, Failed 0, line coverage 98.4% (run before the last small edit: removal of one duplicate test and one rename; the targeted run after it is green).
- `aislop scan .`: 99/100, exactly the four baseline warnings (NativeSeamIsolationFixtures.cs:73,:79; CachedBlockDeletionOutcome.cs:8,:10). A formatting warning on WatchHarness.cs (my sed insert had LF endings in a CRLF file) was fixed.

## Behavior notes and concerns
1. FailedRestart_AfterAReplacedFaultedDrive_ThrowsTheRestartFailureAndReadsFaulted (was FailedRearm_PreservesPreviouslyOutstandingDriveFault): the base test asserted the OLD fault survives when the re-arm fails after a successful swap. In the code, the restart supersedes the faulted instance (dropping its outstanding fault) and a failed start's `RefusedStartFault` is never rethrown by stop, so the old fault is lost and `StopWatchingAsync` will not rethrow anything. I did NOT pin the stop behavior; the port asserts only the rescan's exception, WatchFailureMessage and Faulted catch-up. Suspected design question for the lead/spec owner: should the replaced instance's fault survive a failed restart? Spec 2.6.4 does not say.
2. FileIndexWatchRescanFaultDuringProductionTests: the base scenario (a fault recorded while the producer runs) cannot occur per-drive, because a rescan awaits the old pump's drain before production. The port pins the per-drive equivalent: the retiring pump's apply failure is not recorded (no WatchFaulted, no checkpoint-loss check, no WatchFailureMessage) and the healthy watch restarts from the old cursor after a failed production. This follows spec 2.6.2 scoping rule.
3. Cache-declined cases: a rescan of a blockless drive adopts the block but starts no watch unless a start was requested (WatchRequested), which differs from the base session behavior; ported accordingly (adopt, no watch, later explicit start).
4. `_rescanGate` is still index-wide on this base (rescans of different drives serialize); no ported test depends on it either way.
5. B7 should port the dropped aggregate catch-up case (all-drives WaitForCatchUpAsync waits for an adopted drive).
6. RescanAsync_WhoseSwapFails / OperationCanceledException producer failures rethrow as before (asserted AreSame); no B1 defect found.

## Files changed
- MFTLib.Tests/Index/FileIndexWatchRescanTests.cs (new)
- MFTLib.Tests/Index/FileIndexWatchFailedRescanTests.cs (new)
- MFTLib.Tests/Index/FileIndexWatchRescanFaultDuringProductionTests.cs (new)
- MFTLib.Tests/Index/FileIndexWatchRescanCheckpointLossTests.cs (new)
- MFTLib.Tests/Index/FileIndexWatchRecoveryFaultTests.cs (new)
- MFTLib.Tests/TestSupport/WatchHarness.cs (one property added)

## Fix round 1
Lock re-acquired; HEAD was d594e98 before the fix. Test files only; no production edits or mutations.

Restored trailing `StopWatchingAsync(X)` (completes without throwing, drive watching, nothing outstanding):
- FileIndexWatchRescanTests: RestartsOnlyTheRescannedDrive (T, U); NeverStopsTheOtherDrive (T, U); Rescan_OfT_LeavesUsPumpRunning (T, U); DropsABatchThePumpHadAlreadyAccepted (T); WhoseSwapFails (T, U); WhoseSwapAndWatchRestartBothFail (U only); WhoseWatchRestartFailsAfterTheSwapSucceeded (U only); CacheDeclinedDrive_AdoptsIt... (T, U); RescannedASecondTime (T, U); WhenOnlyInitialDriveFaults (T, after the existing U rethrow assertion).
- FileIndexWatchFailedRescanTests: FailedRescan_PreservesFaultAndDoesNotRecover (U); HealthyDrive_ProducerFailure_RestoresOldWatch (T, U); SuccessfulRetry_AfterFailedProduction (T).
- FileIndexWatchRescanCheckpointLossTests (T, alongside the existing U rethrow); FileIndexWatchRecoveryFaultTests RecoveringOneDrive_PreservesAnotherDrivesEarlierOutstandingFault (E).
Asserting stop on a non-watching drive: CacheDeclined WhoseScanFails ends with U stop no-throw and `StopWatchingAsync('T')` throwing InvalidOperationException, because T was never started (the base stop was session-wide).
Left unpinned, with a comment in the test: stop of T after a failed watch restart (BothFail and RestartFails tests), because the contract does not settle it (review concern 1). Base tests without a trailing stop (NoWatch, CancelledSwapGate, StreamEnds, Unhandled) are unchanged.
Results: five classes Passed 29, Failed 0. Whole suite 1604 total, 1598 passed, 6 skipped, 0 failed, 98.4% line coverage. aislop 99/100, only the four baseline warnings. No restored assertion failed.
