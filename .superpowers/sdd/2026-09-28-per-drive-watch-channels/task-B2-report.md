# Task B2 report: port index watch tests

Status: DONE. Branch task/265-B2, base df50af956570b3d167271ac45395e18131c53329 (verified equal to `git rev-parse df50af9` in impl-265).
Commit: 13a6038 "Port FileIndex watch, pump, fault and catch-up tests to per-drive watches" (renames and drops listed in its message).

## Implemented
Created, in MFTLib.Tests/Index: FileIndexWatchTests.cs, FileIndexWatchPumpTests.cs, FileIndexWatchFaultTests.cs, FileIndexWatchCatchUpTests.cs, FileIndexWatchCatchUpLinkedWaitTests.cs.
FileIndexWatchCatchUpRetentionTests.cs not ported (per brief). Test support: no member added or changed (FakeIndexWatchSource, WatchHarness, ScriptedDriveWatch untouched). No production change.

New methods with no base counterpart (per-drive analogs): StartWatchingAsync_EachDriveStartsFromItsOwnHeaderCursor, CallerTokenCancellation_AfterStart_LeavesTheWatchRunning. Not mutation-verified (brief names no new tests).
DisposeAsync_StopsTheWatchBeforeReleasingBlocks was rewritten to prove ordering directly: a Changed handler parks the pump on a gate, DisposeAsync stays incomplete with the block still mapped, then completes after release.

## Verification
- Ports are not test-first. Targeted: `dotnet test MFTLib.Tests -c Release -p:Platform=x64 --filter "FullyQualifiedName~FileIndexWatchTests|...PumpTests|...FaultTests|...CatchUpTests|...CatchUpLinkedWaitTests"` -> Passed! Failed: 0, Passed: 59, Total: 59. No ported case failed, so no suspected B1 defect.
- Whole suite `.\scripts\run-coverage.ps1 -NonInteractive`: Total 1634, Passed 1628, Skipped 6 (pre-existing Linux/Unix platform skips), Failed 0; line coverage 98.6%.
- `aislop scan .`: 99/100, exactly the four baseline warnings (NativeSeamIsolationFixtures.cs:73, :79; CachedBlockDeletionOutcome.cs:8, :10). Nothing else.
- Base-vs-new method audit was built mechanically (comm of `[TestMethod]` names per file); every difference is in the commit message.

## Notes for the tranche audit
- All 10 `WaitForCatchUpAsync_AllDrives_*` cases and the coordinator-collection case are dropped for B7 (no-list wait, batched wait).
- A start failure (source throws) is asserted to raise no WatchFaulted (Pump.StartWatchingAsync_ReportsAnIndependentSourceOperationCanceledExceptionThroughTheStart, 0 faults). This matches B1 code and the existing per-drive test; B7/B8 should keep it.
- A fault-then-fresh-start stop (HandleEndingWithoutAStop_FaultsTheDriveAndAFreshStartRecovers) asserts the stop after a superseded faulted instance completes without rethrowing; it passes.
- Files are CRLF, matching the tree.
