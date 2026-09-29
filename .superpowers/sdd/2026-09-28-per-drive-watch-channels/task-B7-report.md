# Task B7 report: batched entry points

Branch `task/265-B7`, base `d1a20a999a45ad67557ea9536e759d8a1da73e98` (verified), commit `28f98ed`
"Batched FileIndex operations fan out per drive and return one result each".

## What was implemented

- `MFTLib/Index/DriveOperationResult.cs`: `DriveOperationOutcome { Succeeded, Failed, NotApplicable }`,
  `DriveOperationResult(char DriveLetter, DriveOperationOutcome Outcome, Exception? Failure)`.
- `MFTLib/Index/FileIndex.Batched.cs`: the eight signatures of the brief, verbatim. Every form goes through
  one `RunBatchAsync`: `ArgumentNullException` for a null list, `ObjectDisposedException`, `ArgumentException`
  for an unknown or duplicate (case-insensitive) letter, all before any drive starts; then each drive's
  single-drive operation runs concurrently; `Task.WhenAll` of per-drive results (each captures its exception
  as `Failed`); the call throws `OperationCanceledException(token)` only after everything settled and only when
  at least one drive's failure was an `OperationCanceledException` and the token is cancelled.
  The no-list forms take `_options.Drives` order.
- `NotApplicable` is decided by a predicate evaluated per drive under `_stateLock` right before the single-drive
  call: start `FindWatchableDriveBlockLocked(letter) is null`; stop `!IsWatchingLocked` (the same expression
  `StopWatchingAsync` now throws on, in `FileIndex.WatchDrive.cs`); catch-up wait `!HasCatchUpToAwaitLocked`
  (mirrors the cases `WaitForCatchUpAsync(char)` answers; that method is unchanged). A state change between
  predicate and call surfaces as `Failed` carrying the single form's `InvalidOperationException`.
  Rescan has no `NotApplicable`. Single-drive forms still throw plain `InvalidOperationException`
  (an earlier draft used a private subclass; 9 existing tests use MSTest's exact-type assertion, so it was dropped).
- Batched wait: composed over the single wait, which already delivers completion through `AwaitQueuedAsync`
  (a `RunContinuationsAsynchronously` source fed by token registrations, no `Task.WaitAsync`), so continuations
  never run on the settling pump stack. `BatchedWait_SettledByPumpFault_ContinuationNotInline` proves it.
- B5's single-drive rescan throws (`InvalidOperationException` "Drive X was not rescanned: ..." with the producer
  exception inside; `JournalCatchUpLostException` after a lost-catch-up stop) were NOT reimplemented.
- 38 `cref="RescanAsync"` / `StartWatchingAsync` / `StopWatchingAsync` doc references (MFTLib and two test files)
  now name `(char, CancellationToken)`, because the bare name became ambiguous (jb InvalidXmlDocComment).
- Test support: `FakeIndexWatchSource.HoldStartFor(char, TestGate, observeToken)` and `FailStartFor(char, Exception)`
  (per-drive, so concurrent starts stay deterministic); `WatchHarness.WithBlocklessDrives(char[], params char[])`
  (producer fails the listed drives' scans, so they open `Failed` with no MFT-backed block).

## Tests added (file `MFTLib.Tests/Index/FileIndexBatchedOperationTests.cs` unless noted)

The 12 named by the brief: `BatchedStart_PartialFailure_ReportsPerDrive_DoesNotThrow`,
`BatchedStart_CancelledWhileGated_ThrowsOnlyAfterEverySettles_NoHandlePublished`,
`Batched_DuplicateLetter_ThrowsArgumentBeforeStarting`, `Batched_UnknownLetter_Throws`, `Batched_Null_Throws`,
`Batched_Disposed_ThrowsObjectDisposed`, `NoListForm_CoversDrivesInOptionsOrder`, `BatchedStop_ReturnsFaultAsFailed`,
`BatchedWait_ReturnsPerDrive`, `BatchedRescan_OneDriveStopsAfterThreeLostCatchUps_ReportsFailedWithMessage_OthersSucceed`,
`SingleRescan_CatchUpStop_ThrowsJournalCatchUpLostException`, `SingleRescan_ProducerReturnsNoBlock_ThrowsWithFailureMessage`;
plus `BatchedWait_SettledByPumpFault_ContinuationNotInline`.

Ported (new file `FileIndexBatchedWaitTests.cs`, restated for the per-drive contract; a batched wait returns after
every drive settled and reports per drive, so a name changes when the old meaning does):

| Old name (`c1d43784`) | New name | Meaning change |
|---|---|---|
| `..._AllDrives_WaitsForTheSlowestDrive` | same | none (results all Succeeded) |
| `..._AllDrives_WithASingleWatchedDrive_CompletesWithItsCatchUp` | same | added: not complete before the catch-up |
| `..._AllDrives_WhenEveryDriveAlreadyCaughtUp_IsCompleteAtIssue` | same | none (synchronously complete) |
| `..._AllDrives_ThrowsWhenNoWatchIsRunning` | `..._AllDrives_WhenNoWatchIsRunning_ReportsEveryDriveNotApplicable` | throw became NotApplicable |
| `..._AllDrives_FaultsImmediatelyWhenFirstDriveFaultsWhileSecondIsCatchingUp` | `..._WhenFirstDriveFaultsWhileSecondIsCatchingUp_ReportsItFailedAfterTheSecondSettles` | no early fault; Failed after all settle |
| `..._AllDrives_DriveFaultsAfterCatchUpWhileOtherDriveIsCatchingUp` | `..._DriveFaultsAfterCatchUpWhileOtherDriveIsCatchingUp_StillReportsItSucceeded` | caught-up drive stays Succeeded |
| `..._AllDrives_DriveReArmedAfterCatchUpWhileOtherDriveIsCatchingUp_CancelsAggregateWait` | `..._DriveReArmedAfterCatchUpWhileOtherDriveIsCatchingUp_StillReportsItSucceeded` | same |
| `..._AllDrives_DriveFaultsAfterCatchUpThenOtherDriveCatchesUp_FaultsAggregateWait` | `..._DriveFaultsAfterCatchUpThenOtherDriveCatchesUp_ReportsBothSucceeded` | same |
| `..._AllDrives_DriveFaultsWhileCatchingUpThenOtherDriveCatchesUp_FaultsAggregateWait` | `..._DriveFaultsWhileCatchingUpThenOtherDriveCatchesUp_ReportsTheFirstFailed` | Failed (`DriveWatchFaultException` wrapping the IOException) |
| `..._AllDrives_DriveReArmedWhileCatchingUpThenOtherDriveCatchesUp_CancelsAggregateWait` | `..._DriveReArmedWhileCatchingUpThenOtherDriveCatchesUp_ReportsTheFirstCancelled` | Failed carrying `OperationCanceledException` |

Also: the all-drives half of `WaitForCatchUpAsync_DriveFaultsAfterCatchUp_ReturnsFaultedTask` ->
`WaitForCatchUpAsync_AllDrives_DriveFaultsAfterCatchUp_ReportsItFailedToALaterWait`; new
`WaitForCatchUpAsync_AllDrives_CancelledToken_ThrowsAfterEveryDriveSettles`.
B3's dropped aggregate case, in `FileIndexWatchRescanTests.cs`, name kept:
`RescanAsync_CacheDeclinedDriveWhileWatching_AggregateWaitForCatchUp_WaitsForAdoptedDrive` (T is adopted by the
rescan and joins by its own `StartWatchingAsync('T')`; the no-list wait then covers both).
C6's dropped assertion: the batched `WaitForCatchUpAsync(token)` late wait added to
`WatchFailureObservationTests.RunIndexScenarioAsync` (`requestLateWaits`): `Failed`, same fault exception.
"Failed source start raises no WatchFaulted": asserted in `BatchedStart_PartialFailure_...` (`harness.Faults.Count == 0`).
Not ported: `LinkedWait...AllDrivesWithoutACallerToken_WhenDisposed_CancelsTheCoordinator` (coordinator gone, per B3).

Audit of existing tests that expected a failed rescan to complete normally: Grep of `RescanAsync` callers plus
the whole suite; nothing further needed changing (B5 did it), whole suite green.

## TDD evidence (W40-R1: scratch mutation, not committed, restored and rebuilt after each)

Tests and production were written together (the API had to exist to compile), so RED is by scratch mutation of
`MFTLib/Index/` sources. Every mutation was run with this literal command after `dotnet build MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64`:

`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexBatchedOperationTests|FullyQualifiedName~FileIndexBatchedWaitTests|FullyQualifiedName~AggregateWaitForCatchUp_WaitsForAdoptedDrive|FullyQualifiedName~WatchFailureObservationTests"`

(the CONT2 run used `--filter "FullyQualifiedName~BatchedWait_SettledByPumpFault_ContinuationNotInline|FullyQualifiedName~PumpFaultSettlesWaiter_ContinuationNotInline"`).
GREEN on unmutated code: `Passed! - Failed: 0, Passed: 32, Skipped: 0, Total: 32`.
Real failing output per mutation (test: assertion message):

- Mutation "`applicable` ignored in `RunOneAsync`" (`if (applicable is not null && applicable(driveLetter) && false)`), 4 failed:
  `BatchedStart_PartialFailure_...`: `Assert.AreEqual failed. Expected:<NotApplicable>. Actual:<Failed>.`;
  `BatchedStop_ReturnsFaultAsFailed`: `Expected:<NotApplicable>. Actual:<Failed>. V was never watching`;
  `BatchedWait_ReturnsPerDrive`: `Expected:<NotApplicable>. Actual:<Failed>. V is not being watched`;
  `..._AllDrives_WhenNoWatchIsRunning_ReportsEveryDriveNotApplicable`: `Assert.IsTrue failed.`
- Validation block removed, 4 failed: `Batched_DuplicateLetter_...`, `Batched_UnknownLetter_Throws`,
  `Batched_Disposed_ThrowsObjectDisposed`: `Expected <ArgumentException|ObjectDisposedException> to be thrown.`;
  `Batched_Null_Throws`: `System.NullReferenceException`.
- `AllDriveLetters` reversed, `NoListForm_CoversDrivesInOptionsOrder`: `CollectionAssert.AreEqual failed. (Element at index 0 do not match.)`
- `Task.WhenAll(...).WaitAsync(cancellationToken)` (returns before per-drive settle),
  `BatchedStart_CancelledWhileGated_...`: `Assert.IsFalse failed. V's start ignores the token, so the call has not settled`.
- Final `throw new OperationCanceledException` replaced by `return results;`, 2 failed:
  `BatchedStart_CancelledWhileGated_...` and `WaitForCatchUpAsync_AllDrives_CancelledToken_ThrowsAfterEveryDriveSettles`: `Expected OperationCanceledException to be thrown.`
- `throw noBlock;` -> `return;` (old silent return) in `FileIndex.RescanRestart.cs`, `SingleRescan_ProducerReturnsNoBlock_ThrowsWithFailureMessage`: `Expected InvalidOperationException to be thrown.`
- `throw lost;` -> `return attempt;` in `FileIndex.CatchUp.cs`, `SingleRescan_CatchUpStop_ThrowsJournalCatchUpLostException`: `Expected JournalCatchUpLostException to be thrown.`; `BatchedRescan_OneDriveStopsAfterThreeLostCatchUps_...`: `Expected:<Failed>. Actual:<Succeeded>.`
- `RunOneAsync` reporting `Succeeded` for every failure, 13 failed incl. `BatchedStart_PartialFailure`, `BatchedStop_ReturnsFaultAsFailed`,
  `BatchedWait_ReturnsPerDrive`, `BatchedRescan_...`, the failed-drive ports, and `BrokerFailure_DoesNotLeaveUnobservedInternalTasks (False,True)` / `(True,True)`
  (`Expected:<Failed>. Actual:<Succeeded>.`: the C6 assertion).
- `AwaitQueuedAsync` completion source and `WatchCatchUpSlot.Waiter` both made synchronous (both are needed: either alone still completes
  asynchronously through the other), `BatchedWait_SettledByPumpFault_ContinuationNotInline` and the existing `PumpFaultSettlesWaiter_ContinuationNotInline`:
  `Assert.IsFalse failed. the waiter's continuation ran on the settling stack`.
- `AllDriveLetters().Take(1)` failed 11 (all no-list ports: `WaitsForTheSlowestDrive`: `Assert.IsFalse failed.`, `WhenFirstDriveFaults...`: `the call returns only after every drive has settled`, ...);
  `.TakeLast(1)` failed 12 including `RescanAsync_CacheDeclinedDriveWhileWatching_AggregateWaitForCatchUp_WaitsForAdoptedDrive`: `Assert.IsFalse failed.`
- `await Task.Yield()` in `RunOneAsync`, `..._WhenEveryDriveAlreadyCaughtUp_IsCompleteAtIssue`: `Assert.IsTrue failed.`
- `_ = operation(driveLetter);` (no awaiting the operation), 19 failed incl. `WithASingleWatchedDrive_CompletesWithItsCatchUp`: `Assert.IsFalse failed.`,
  and every result test.

## Whole suite, aislop, guards

- `.\scripts\run-coverage.ps1 -NonInteractive` after the last edit (comment-only edit to `DriveOperationResult.cs` was followed by this run):
  `Total tests: 1859, Passed: 1853, Failed: 0, Skipped: 6` (6 pre-existing admin skips), `Line coverage: 97.8%`. `NamespaceBoundaryTests` inside it.
  (An earlier run, before the exact-type fix, had 9 failures: existing tests asserting exact `InvalidOperationException` against my first subclass design.)
- `aislop scan .` after the last edit: `99 / 100 Healthy 0 errors 5 warnings`: exactly the four baseline warnings
  (`NativeSeamIsolationFixtures.cs:73`, `:79` AsyncFixer01; `CachedBlockDeletionOutcome.cs:8`, `:10`) plus the ruled 8-parameter `JournalBrokerHost` constructor. No other finding.
  (First scan had 46: 38 ambiguous crefs, a blocking call in a test, a redundant doc comment, one null-forgiving `!`; all fixed, none suppressed.)
- All changed files CRLF; no em-dashes.

## Files changed

Production: `MFTLib/Index/DriveOperationResult.cs` (new), `FileIndex.Batched.cs` (new), `FileIndex.WatchDrive.cs` (stop uses `IsWatchingLocked`; crefs),
crefs only in `BlockSource.cs`, `DriveFailureKind.cs`, `DriveStatus.cs`, `FileIndex.CatchUp.cs`, `FileIndex.Rescan.cs`, `FileIndex.RescanRestart.cs`,
`FileIndex.Scanning.cs`, `FileIndexOptions.cs`, `JournalCatchUpLostException.cs`, `JournalCheckpointLoss.cs`, `WatchFault.cs`.
Tests: `FileIndexBatchedOperationTests.cs`, `FileIndexBatchedWaitTests.cs` (new), `FileIndexWatchRescanTests.cs` (+1 test), `WatchFailureObservationTests.cs` (+3 lines),
crefs in `FileIndexCacheOnlyUnresumableWatchTests.cs`, `FileIndexOpenProgressTests.cs`, `TestSupport/FakeIndexWatchSource.cs`, `TestSupport/WatchHarness.cs`.

## Decisions and concerns

- Owner spec gaps untouched: stop after a failed restart, and whether a fresh start discards a faulted instance's fault, keep B1/B5/B6 behavior. A batched stop of a drive whose watch faulted reports `Failed` with that fault (the stop rethrows it once), as the spec says.
- Choice named: the brief's "`V` an enumeration-backed drive" is a drive with no MFT-backed block; the test harness cannot mix producer policies inside one index (policy is per index), so V is a drive whose scan failed at open (`DriveState.Failed`, no block). Same `NotApplicable` path (`FindWatchableDriveBlockLocked` is null).
- Choice named: batched cancellation throws only when some drive's failure was an `OperationCanceledException` and the token is cancelled; a cancelled token that stopped nothing returns the results.
- `BatchedStart_CancelledWhileGated_...`: T and U are gated behind held rescans (lifecycle gates) and never reach the source; V's source start ignores the token and does publish its handle after release (V `Succeeded`); "no handle published" is asserted for the gated drives T and U.
- `NotApplicable` predicates and the single-drive throw conditions are two spellings for wait (`HasCatchUpToAwaitLocked` mirrors `WaitForCatchUpAsync`'s chain); start and stop share their expressions. A drift-catching test for a wait on a drive with only a refused start is not added.
- Adjacent: `AGENTS.md` still describes the pre-plan session model and does not list the batched forms (D tasks); `index-port-common.md` line "There is NO automatic recovery yet" is stale (noted by B6). `FileIndexWatchRescanTests.cs` is over 500 lines already (580 before, 604 now).

## Primary checkout

`git -C C:\Users\mtsch\MFTLib status --short`: (empty output)

## Fix round 1

Commit: "Batched catch-up wait owns an asynchronous completion and a cancelled batch always throws" (on 28f98ed).

1. Batch-owned completion: new `FileIndex.BatchedCatchUpWait.cs` (nested `BatchedCatchUpWait`). It follows each drive's catch-up task
   (`GetCatchUpWaitLocked`, now shared with the single `WaitForCatchUpAsync`, Minor 1), settles per drive, and completes a
   `TaskCompletionSource` created with `RunContinuationsAsynchronously`; caller and disposal tokens arrive through `Register`, never `Task.WaitAsync`.
   RED for `BatchedWait_SettledByPumpFault_ContinuationNotInline`, literal command:
   `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BatchedWait_SettledByPumpFault_ContinuationNotInline|FullyQualifiedName~PumpFaultSettlesWaiter_ContinuationNotInline"`
   - mutation of the batch source ALONE (`new(TaskCreationOptions.RunContinuationsAsynchronously)` -> `new()` in `FileIndex.BatchedCatchUpWait.cs`): `Passed! - Failed: 0, Passed: 2, Total: 2`.
     It cannot fail: the batch is fed by `WatchCatchUpSlot.Waiter`, itself a `RunContinuationsAsynchronously` source, so the batch's settle already runs on a pool thread, never the pump's.
   - mutation of the batch source AND the slot `Waiter`: `Failed BatchedWait_SettledByPumpFault_ContinuationNotInline: Assert.IsFalse failed. the waiter's continuation ran on the settling stack` (`Failed: 1, Passed: 1, Total: 2`; the pre-existing `PumpFaultSettlesWaiter_ContinuationNotInline` is the passing one because it goes through the single wait).
   The two layers are defense in depth; the controller may want the spec line 649 wording to say so.
2. Cancellation: every batched form now ends with `cancellationToken.ThrowIfCancellationRequested()` after all settled (the wait form's `Complete()` cancels its completion when the caller token is cancelled). New `Batched_CancelledTokenWithNothingToDo_StillThrows` (empty and all-NotApplicable, all four forms).
   `BatchedStart_CancelledWhileGated_...NoHandlePublished` restored: U's start gated in the source (observes the token), cancel, throws OperationCanceledException, `Handles.Count == 0`, U `NotStarted`. My earlier scenario is kept as `BatchedStart_CancelledWhileAnotherStartIsInFlight_WaitsForItBeforeThrowing`.
   RED, same full 4-class command as before (`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexBatchedOperationTests|FullyQualifiedName~FileIndexBatchedWaitTests|FullyQualifiedName~AggregateWaitForCatchUp_WaitsForAdoptedDrive|FullyQualifiedName~WatchFailureObservationTests"`):
   - `ThrowIfCancellationRequested` removed: 3 failed (`BatchedStart_CancelledWhileGated_...`, `BatchedStart_CancelledWhileAnotherStartIsInFlight_...`, `Batched_CancelledTokenWithNothingToDo_StillThrows`): `Expected OperationCanceledException to be thrown.`
   - wait form's `if (_callerToken.IsCancellationRequested)` -> `if (false)`: 2 failed (`Batched_CancelledTokenWithNothingToDo_StillThrows`, `WaitForCatchUpAsync_AllDrives_CancelledToken_ThrowsAfterEveryDriveSettles`): `Expected OperationCanceledException to be thrown.`
3. Bounded awaits: every await on an index call in the two batched test files and the moved aggregate test has `.WaitAsync(HangGuard)`; `FakeIndexWatchSource` bounds its gate wait with `WaitAsync(HangGuard, CancellationToken.None)`; the cancellation tests release gates and held scans in `finally`.
4. RED command for the continuation test: recorded in item 1 above.
Minors: catch-up applicability shared (`GetCatchUpWaitLocked`); the aggregate port moved to `FileIndexWatchRescanTests.CacheDeclinedCatchUp.cs` (class made `partial`).

Verification: targeted classes green (37 passed incl. `NamespaceBoundaryTests`). `run-coverage.ps1 -NonInteractive` after the last edit: `Total tests: 1861, Passed: 1855, Failed: 0, Skipped: 6`, line coverage 97.8%. (The first run of this round had 1 failure, `FileIndexResilienceTests.OpenAsync_SecondDriveCancelledMidScan_UnwindsTheFirstDrivesAlreadyAddedBlock`, `FileNotFoundException` on the cache block; it passed 5 of 6 solo reruns and on the clean full rerun, and touches only the open path, not batched code: a pre-existing flake, reported, not fixed.) aislop: 99/100, the four baseline warnings plus the ruled `JournalBrokerHost` warning.
Primary checkout: `git -C C:\Users\mtsch\MFTLib status --short`: (empty)

## Fix round 2

Commit "Disposing the index cancels a pending batched catch-up wait".

1. `BatchedCatchUpWait.Complete()` now also completes cancelled (`TrySetCanceled(disposalToken)`) when the disposal token is cancelled and the caller's is not. New test `WaitForCatchUpAsync_AllDrives_IndexDisposedWhilePending_IsCancelled` (FileIndexBatchedWaitTests.cs).
   RED (scratch mutation `else if (_disposalToken.IsCancellationRequested)` -> `else if (false)`, not committed, restored), literal command:
   `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexBatchedOperationTests|FullyQualifiedName~FileIndexBatchedWaitTests|FullyQualifiedName~AggregateWaitForCatchUp_WaitsForAdoptedDrive|FullyQualifiedName~WatchFailureObservationTests"`
   Output: `Failed WaitForCatchUpAsync_AllDrives_IndexDisposedWhilePending_IsCancelled [19 ms]  Error Message: Expected OperationCanceledException to be thrown.  Failed! - Failed: 1, Passed: 34, Skipped: 0, Total: 35`. Unmutated: `Passed! - Failed: 0, Passed: 35, Total: 35`.
3. Every `CancelAsync()` in the two batched test files is now `.WaitAsync(HangGuard)`; the aggregate test no longer uses `await using`: it opens the index, and disposes it in a `finally` with `DisposeAsync().AsTask().WaitAsync(HangGuard)`. (`using var harness` disposal was already bounded by `WatchHarness.Dispose`.)

Verification: `run-coverage.ps1 -NonInteractive` after the last edit: `Total tests: 1862, Passed: 1856, Failed: 0, Skipped: 6`, coverage 97.8%. aislop: see reply (baseline only if no other lines listed above).
Primary checkout: `git -C C:\Users\mtsch\MFTLib status --short`: (empty)
