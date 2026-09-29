# Task B4 report

Status: DONE. Commit 0f7e292 on task/265-B4, base df50af9 (confirmed equal to impl-265 df50af9).

## Implemented
Test-only. Created FileIndexCacheOnlyUnresumableWatchTests.cs, FileIndexMidSessionCheckpointLossTests.cs,
ConsumerJournalIsolationTests.cs. FileIndexCheckpointLossDetectionTests.cs already existed (B1 ported it),
compiles and passes: left alone. No test-support additions, no production change.

## Contract findings worth knowing (observed, pinned in tests)
- StartWatchingAsync(X) on an unresumable drive THROWS InvalidOperationException (message names RescanAsync),
  sets WatchFailureMessage and reads Faulted, but requests no watch. Consequences: StopWatchingAsync(X) then
  throws InvalidOperationException (not watching); WaitForCatchUpAsync(X) faults with the refusal;
  RescanAsync(X) clears the refusal but does NOT start the drive (WatchRequested is false); a later
  StartWatchingAsync(X) starts from the fresh cursor. The old "arms onto the running watch" case is renamed accordingly.
- Possible spec discrepancy (not changed): spec 2.6.4 says a rescan whose producer returns no block throws
  InvalidOperationException with MftProducerFailureMessage. Code returns normally when the drive required a
  replacement (unresumable/faulted); the ported test keeps the base assertion (completes, message on DriveStatus).
- The harness fixes cursor (journal 7, usn 100), so mid-session tests use ArmedUsn = WatchHarness.NextUsn; all
  offsets are relative so assertions are unchanged.

## Method accounting (base c1d43784 vs new)
Renamed: 5 (listed in commit message). Dropped: RescanAsync_StartedBeforeAnyWatchSession_..._MidScan (session
capture), WatchFaultOnADriveWithNoBlock_QueriesNoJournal (needs a shared stream). Added: WatchFaultOnT_LiveWatchLossRecordedBeforeWatchFaultedRaised.
ConsumerJournalIsolationTests: all 4 methods (10 data rows) ported, name unchanged; FakeIndexWatchSource calls
adapted (Starts, HandleFor, FailDrive), unresumable start now asserted as a thrown refusal.
Override callbacks: ConcurrentDictionary / ConcurrentQueue; the rest are stateless.

## TDD evidence (new test only)
Scratch mutation, not committed: in FileIndex.WatchPump.cs RecordPumpFault, swapped
RecordCheckpointLossForFaultedDrive(...) and RaiseWatchFaulted(fault). Run of FileIndexMidSessionCheckpointLossTests:
Failed WatchFaultOnT_LiveWatchLossRecordedBeforeWatchFaultedRaised ("the handler must see the loss the fault found")
and Failed WatchFaultOverATrimmedCheckpoint_... (ported case fails only by race-free ordering of its assertion read).
Reverted; git diff clean. Unmutated: 9/9 pass.

## Verification
- Targeted (4 classes incl. Detection): 26 passed, repeated 5 times, stable.
- Whole suite `.\scripts\run-coverage.ps1 -NonInteractive`: exit 0, 1598 tests, line coverage 98.42%.
- aislop scan --staged: 99/100, only the two NativeSeamIsolationFixtures.cs:73/:79 baseline warnings. (My first pass
  had AsyncFixer01, AccessToDisposedClosure, CA1859 in my file; fixed.) Whole-tree `aislop scan .` not run separately.
- Note: `dotnet build` of the whole solution fails on the vcxproj here (expected); built MFTLib.Tests.csproj directly after MSBuild native.

## Concerns / adjacent
- The dropped session test's per-drive intent (a start issued during the drive's rescan waits on the lifecycle gate)
  is covered by FileIndexPerDriveWatchTests.Lifecycle Rescan_HoldsLifecycleGateThroughProduction, not re-ported.
