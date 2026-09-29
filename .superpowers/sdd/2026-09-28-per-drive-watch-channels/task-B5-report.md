# Task B5 report: gate split, publish under _stateLock, lost catch-up, disposal order

Status: DONE_WITH_CONCERNS (concerns: RED evidence for two tests, see "TDD evidence"; a scratch
mutation to produce it was denied by the auto-mode classifier).

Worktree `C:\Users\mtsch\MFTLib-worktrees\265-B5`, branch `task/265-B5`, base
`566718bb6f80795a6f58e50ee3441dc81220cdaf` (verified). Commit `5d459cd` "Split FileIndex gates per
drive, publish snapshots under the state lock and retry a drive three times after a lost catch-up".

## What was implemented

- **Gates per drive.** `_rescanGate` and `_swapGate` deleted. `DriveRuntime` gains `WriteGate`
  (`SemaphoreSlim`), `ConsecutiveLostCatchUps` and `RetryingLostCatchUp` (both under `_stateLock`);
  its class remark states the lock order. `ApplyJournalEntriesCore` takes only its drive's write
  gate (synchronous `Wait`); the instance check runs under it.
- **Publish under `_stateLock` (R8).** New `FileIndex.Publication.cs`: `PendingDriveResult`
  (block, block source, access-denied count, producer failure and message, discarded block,
  checkpoint loss, cache slot, cache-only-unresumable flag, `CatchUpLoss`), `BuildDriveBlock`,
  `RecordPendingResultLocked` (writes every ordinal-keyed field and the count in one step), and
  `PublishRescannedBlockAsync` (drive write gate, then `_stateLock`: assign ordinal - existing or
  `_driveBlocks.Count` - build the `DriveBlock`, `Snapshot.Create` over a copy before committing,
  commit, record, retire the previous snapshot). `PublishSnapshotLocked`/`RetireCurrentSnapshotLocked`
  in `ScanCleanup.cs` own `_retiredSnapshots` mutation under `_stateLock`.
- **Production returns a result, keyed by nothing.** `ProduceDriveBlockAsync` returns a
  `PendingDriveResult` (block + `CatchUpLoss`, or the producer failure and message); the caller
  builds the `DriveBlock`. The open path (`AddDriveAsync`) settles into a `PendingDriveResult` and
  adopts it under `_stateLock` (`AdoptOpenedDrive`); a blockless drive is recorded by letter
  (`RecordFailedDrive(letter, kind, pending)`), so no failure message, discarded reason or
  checkpoint loss is keyed by a tentative ordinal any more. `WarmStartResult` carries the block
  file, the loss and the cache-only flag.
- **Rescan.** `RescanAsync` links its token to the disposal token, holds only the drive's lifecycle
  gate, and runs the scan operation (`RunScanOperationAsync`, `FileIndex.CatchUp.cs`) whose attempts
  are `ScanAndPublishAsync` (rename-aside, produce, publish, restore on any non-replacement). A
  producer that returns no block now makes `RescanAsync` throw
  `InvalidOperationException("Drive X was not rescanned: <producer message>", producerException)`
  (ruling B4-Q1), after the healthy-watch restart from the old cursor.
- **Lost catch-up (L1).** `public const int FileIndex.LostCatchUpRecoveryLimit = 3`;
  `WatchFaultKind { Subscriber, Drive, Apply, CatchUpLost, Channel, Recovery }` (N-4);
  `WatchCatchUpState.Recovering` between `CaughtUp` and `Faulted`; `DriveStatus.ConsecutiveLostCatchUps`;
  public `JournalCatchUpLostException(driveLetter, consecutiveLostCatchUps, recoveryStopped,
  checkpointLoss, message)` in `MFTLib.Index`. The publish step counts (+1 lost, 0 held, unchanged
  with no block), marks a lost block unresumable (`_unresumableCheckpointsByOrdinal`, a dictionary
  with reason `CacheOnlyAdoption | LostCatchUp`, replacing `_cacheOnlyUnresumableCheckpointOrdinals`)
  and records the loss as the drive's `CheckpointLoss` (it replaces any older report; only the first
  attempt of a consumer rescan whose catch-up holds clears the report). After a lost publish the
  operation sets `Recovering` when `WatchRequested`, raises `WatchFaulted(CatchUpLost, X)` with no
  write gate and no `_stateLock` held (lifecycle gate still held), and rescans at once while the
  count is below 3. At 3 or more: `RefusedStartFault` and `WatchFailureMessage` carry the
  exception (drive reads `Faulted`, keeps its block, `Ready`), and `RescanAsync` throws it. A start
  on a lost-catch-up block is refused with "Drive X cannot be watched: N scans in a row lost their
  journal catch-up ... Call FileIndex.RescanAsync ...". The index reads no journal for any of this.
- **Disposal (spec 5).** Set `_disposed`, cancel the token (rescans linked), retire and drain every
  watch, then take every lifecycle gate, then every write gate, ascending drive letter, release
  retired then current snapshots, then give every gate back (released, never disposed).
- **Docs.** Remarks naming `_swapGate` rewritten (`FileIndex.cs`, `FileIndex.Watch.cs`,
  `ReportedReasonCycles.cs`, `docs/index-format.md`); `DriveStatus` remarks describe the
  `ScanCatchUp` report the way `LiveWatch` is described.
- **Test seams (internal):** `WaitForDriveWriteGateForTest`, `ReleaseDriveWriteGateForTest`,
  `AreDriveGatesFreeForTest` (DriveRuntime.cs), `PublishInsideWriteGateForTest` (CatchUp.cs),
  `ApplyJournalEntriesInsideWriteGateForTest` (Watch.cs, after the instance check and snapshot read).
- **Test support extension:** `WatchHarness.ScriptScans(char, params ScriptedScan[])` with
  `ScriptedScan(CatchUpLoss, Failure, Hold)` and `WatchHarness.ProductionCount(char)`.

## TDD evidence

New tests (24 cases): `FileIndexCatchUpLossTests` (13 incl. the `.Operations.cs` partial),
`FileIndexConcurrentRescanTests` (8 cases), `FileIndexDisposalOrderTests` (3). All brief-named cases
exist; extras: `StartDuringRescanOfUnresumableDrive_JudgesTheFreshBlock` (dispatch's B4 gap) and
`Dispose_TakesLifecycleGatesThenWaitsForABatchHoldingItsWriteGate`.

RED: the public surface and seams were added first as behavior-free stubs on the base code (seams
placed inside `_swapGate` where the base had it), then:

```
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexCatchUpLossTests|FullyQualifiedName~FileIndexConcurrentRescanTests|FullyQualifiedName~FileIndexDisposalOrderTests"
Failed!  - Failed:    22, Passed:     2, Skipped:     0, Total:    24
```

Per test (expected reason in brackets):
- Rescan_CatchUpLostOnce_RetriesAtOnceAndKeepsTheReport: `Assert.AreEqual failed. Expected:<2>. Actual:<1>` [no retry on base]
- Rescan_CatchUpLostThreeTimes_StopsAfterThreeProducerCalls, Rescan_ManualRescanAtTheLimit_MakesOneAttempt,
  CatchUpLost_JournalRecreated_ReportHasNoSuggestion, CatchUpLost_ProducerFailure_IsNotCounted,
  StartDuringRescanOfUnresumableDrive_JudgesTheFreshBlock: `Expected JournalCatchUpLostException to be thrown.` [no limit on base]
- Rescan_SuccessResetsTheCount: `Expected:<2>. Actual:<1>. a later single loss retries`
- CatchUpLostCount_SurvivesOperations, CatchUpFailureNotProven_ScanFailsWithNoBlock_CountUnchangedNoReport:
  `Expected InvalidOperationException to be thrown.` [base returned normally after a producer failure, B4-Q1]
- CatchUpLost_ScanCatchUpReportReplacesLiveWatchReport: `Assert.AreEqual failed. Expected:<JournalCheckpointLoss { ... ScanCatchUp ...` [base kept LiveWatch]
- CatchUpLost_CountsArePerDrive, CatchUpLost_HandlerRunsWithLifecycleGateHeld: `TimeoutException` [no retry, the held retry never entered]
- ConcurrentRescans_..., ConcurrentBlocklessAdoption_DistinctOrdinals_..., ..._OneFailsOneSucceeds_... (both rows),
  ..._BothFail_...: `TimeoutException` [`_rescanGate` serialized the producers; the second never entered]
- PublishForU_MidBatchOnT_IsSafe: `TimeoutException` [U's commit waited on `_swapGate` held by T's batch]
- ApplyJournalEntries_TakesOnlyItsDrivesWriteGate: `Expected:<False>. Actual:<True>. the apply holds T's write gate`
- Dispose_DuringGatedRescans_..., Rescan_TokenLinkedToDisposal: `TimeoutException` [disposal waited out unlinked rescans]
- Dispose_TakesLifecycleGatesThenWaitsForABatchHoldingItsWriteGate: `TaskCanceledException` [base disposal never took lifecycle gates; the poll timed out]

The two that passed on base were tightened and re-run on base (`--filter "FullyQualifiedName~BatchOnT_DoesNotWaitForUCommit|FullyQualifiedName~CatchUpLost_LeavesNoBlockFileBehind"`):
- CatchUpLost_LeavesNoBlockFileBehind: `Assert.AreEqual failed. Expected:<4>. Actual:<2>. the open's scan, then two lost catch-ups and the scan that held`
- BatchOnT_DoesNotWaitForUCommit: `Assert.IsFalse failed. T's batch applied while U's commit still held U's write gate`

Concerns about RED:
- **BatchOnT_DoesNotWaitForUCommit**: its base RED above is not valid evidence. On first GREEN run it
  failed against the new code too, because `RescanAsync('U')` ran synchronously up to the held seam on
  the test's own thread. The test now starts the rescan with `Task.Run`. I then tried the scratch
  mutation (make `ApplyJournalEntriesCore` take one shared write gate, emulating the index-wide gate)
  and the auto-mode classifier denied the edit ("Security Weaken"), so the corrected test has no RED
  evidence. It passes against the new code 5 of 5 runs.
- **StartDuringRescanOfUnresumableDrive_JudgesTheFreshBlock**: its base RED is the absence of the
  lost-catch-up refusal, not the "judge the fresh block" rule it pins. The pinning mutation (judge
  unresumability before taking the lifecycle gate) was not attempted after the denial above.
- Test bodies changed after RED in shape only: the handler-driven harness calls moved to
  `ScriptScans` (to clear `jb/AccessToDisposedClosure`); assertions are unchanged.

GREEN:
```
dotnet test ... --filter "FullyQualifiedName~FileIndexCatchUpLossTests|FullyQualifiedName~FileIndexConcurrentRescanTests|FullyQualifiedName~FileIndexDisposalOrderTests|FullyQualifiedName~FileIndexDisposalRaceTests"
Passed!  - Failed:     0, Passed:    27, Skipped:     0, Total:    27   (5 consecutive runs)
dotnet test ... --filter "FullyQualifiedName~Index"
Passed!  - Failed:     0, Passed:   823, Skipped:     6, Total:   829
```

## Whole suite, coverage, aislop

`.\scripts\run-coverage.ps1 -NonInteractive` (final tree): Total tests 1678, Passed 1672, Skipped 6,
Failed 0. Line coverage 97.5%, branch 94.6% (2182 of 2305), `MFTLib.Index.FileIndex` 98.6%.

`aislop scan . -d`: `99 / 100 Healthy 0 errors 5 warnings` - exactly the four baseline warnings
(`NativeSeamIsolationFixtures.cs:73`, `:79` AsyncFixer01; `CachedBlockDeletionOutcome.cs:8`, `:10`)
plus the ruled `JournalBrokerHost` 8-parameter warning. No other finding.

## Existing tests changed

- `_swapGate` reflection replaced by `WaitForDriveWriteGateForTest`/`ReleaseDriveWriteGateForTest`
  in `FileIndexRescanCleanupTests` (2 cases), `FileIndexWatchRescanTests` (1),
  `FileIndexWatchFailedRescanTests` (1); `SwapGateOf` helpers deleted.
- B4-Q1 (a rescan whose producer returns no block throws): `FileIndexWatchFailedRescanTests`
  (FailedRescan_PreservesFaultAndDoesNotRecover(false), HealthyDrive_ProducerFailure_RestoresOldWatch,
  SuccessfulRetry_AfterFailedProduction_ReplacesAndCatchesUp), `FileIndexWatchRescanTests`
  (RescanAsync_CacheDeclinedDriveWhileWatching_WhoseScanFails_...), `FileIndexProducerSelectionTests`
  (3 cases), `FileIndexWatchRescanFaultDuringProductionTests` (1), `FileIndexRescanCleanupTests`
  (locked replacement case), and the ported "scan fails without throwing" case in
  `FileIndexCacheOnlyUnresumableWatchTests`, renamed
  `RescanAsync_OnTheUnresumableDrive_WhenTheProducerReturnsNoBlock_ThrowsAndLeavesTheRefusalIntact`.
- `FileIndexDisposalRaceTests.RescanAsync_AdmittedToTheGateAfterDisposeAsync_ThrowsInsteadOfPublishing`
  pinned "disposal waits out a rescan"; spec 5 cancels rescans instead. Renamed
  `RescanAsync_QueuedOnTheGateWhenDisposeAsyncBegins_IsCancelledInsteadOfPublishing`: both rescans end
  with `OperationCanceledException`, disposal completes, the unreplaced block is released.
- `FileIndexWatchRescanTests.WriteMftShapedBlock` made `internal` for reuse.

## Files changed

Production: `MFTLib/Index/FileIndex.cs`, `FileIndex.CatchUp.cs` (new), `FileIndex.Publication.cs`
(new), `FileIndex.Disposal.cs`, `FileIndex.DriveRuntime.cs`, `FileIndex.Rescan.cs`,
`FileIndex.RescanRestart.cs`, `FileIndex.ScanCleanup.cs`, `FileIndex.Scanning.cs`,
`FileIndex.Watch.cs`, `FileIndex.WatchCatchUp.cs`, `FileIndex.WatchDrive.cs`,
`FileIndex.WatchTargets.cs`, `JournalCatchUpLostException.cs` (new), `DriveStatus.cs`,
`WatchFault.cs`, `WatchCatchUpState.cs`, `ReportedReasonCycles.cs` (doc), `docs/index-format.md` (one line).
`JournalCheckpointLoss.cs` untouched (C1-Q1). `BlockWriter.Complete` untouched (W4-3).
Tests: the three new classes (four files), `TestSupport/WatchHarness.cs`, and the eight existing
files listed above. All `.cs` files CRLF.

## Lock order

Order, outermost first: drive X's lifecycle gate, X's write gate, `_stateLock`. No site acquires a
gate while holding `_stateLock`; no site but disposal holds two drives' gates.

Lifecycle gate:
- `RescanAsync` (Rescan.cs): X's gate from entry to exit; inside it takes X's write gate (publish) and
  `_stateLock` (many short sections). Raises `WatchFaulted(CatchUpLost)` holding only this gate.
- `StartWatchingCoreAsync` (WatchDrive.cs) when `gateHeld` is false; a rescan's restart passes
  `gateHeld: true` and never reacquires. Inside: `_stateLock` sections only.
- `ReleaseSnapshotsForDisposalAsync` (Disposal.cs): every drive, ascending letter, then every write
  gate ascending, then `_stateLock` sections; releases all in `finally`.
- `AreDriveGatesFreeForTest` (test seam): `Wait(0)`/`Release` on the lifecycle then the write gate,
  never holding either while taking the other.

Write gate:
- `ApplyJournalEntriesCore` (Watch.cs): X's gate (`Wait`), then `_stateLock` in `TryGetDriveOrdinal`
  (before the gate) and `IsRunningWatchOverItsBlock` / `CurrentSnapshot` (under it). `Changed` is
  raised after release.
- `PublishRescannedBlockAsync` (Publication.cs): X's gate (`WaitAsync` with the rescan token), then
  `_stateLock` for the whole commit and snapshot publication. Called only with X's lifecycle gate held.
- Disposal as above; `WaitForDriveWriteGateForTest`/`ReleaseDriveWriteGateForTest` (test seams).

`_stateLock` only (no gate taken inside any of them): `Drives`, `CurrentSnapshot`,
`TryGetDriveOrdinal`, `OpenAsync` final publish, `DisposeAsync` (flag; owner locks),
`ReleaseUnpublishedBlocks`, `DescribeSettledDrive` (FileIndex.cs); `AdoptOpenedDrive`
(Scanning.cs); `RecordOfflineDrive`, `RecordFailedDrive`, `EnsureCanonicalOwnership` (ScanCleanup.cs);
`RequiresReplacementForWatchRecovery`, `FindBlockForRescan`, `RecordRescanProducerFailure`
(Rescan.cs); `RetireWatchForRescanAsync`, `ClearRefusedStartAfterReplacement`,
`RestartRequestedWatchAsync` (RescanRestart.cs); `RunScanOperationAsync` finally, `RecordLostCatchUp`
(CatchUp.cs); `StopEveryWatchForDisposalAsync` (Disposal.cs); `CompleteInstanceDrain`
(DriveRuntime.cs); `BorrowCurrentSnapshot` (Queries.cs); `WaitForCatchUpAsync` (WatchCatchUp.cs);
`RecordCheckpointLossForFaultedDrive` (two sections, journal read between them outside the lock,
WatchCheckpointLoss.cs); `StopWatchingAsync`, `StartWatchingWithGateHeldAsync`,
`RegisterStartingInstance`, `AbandonFailedStart`, `TryPublishHandle` (WatchDrive.cs);
`AnnounceSubscriberFault`, `CompleteWatchCatchUp`, `RecordPumpFault` (WatchPump.cs). Pumps hold no
gate when they raise `Changed` or `WatchFaulted`.

## Decisions the brief or spec left open (least surprising choice, named)

- **Stop after a failed restart** (open owner gap): unchanged from B1. A rescan whose healthy-watch
  restart fails throws `AggregateException(scanFailure, restartFailure)`; the refusal is recorded as
  `RefusedStartFault`; a later stop clears the request and that fault without rethrowing it.
- **Fresh start and a faulted instance's outstanding fault** (open owner gap): untouched. At the
  lost-catch-up limit a drive whose current instance is `Faulted` keeps that instance (its
  outstanding fault stays for the stop); `RefusedStartFault` and `WatchFailureMessage` are set too.
- **Open path**: a lost catch-up at open is recorded (count, unresumable mark, `ScanCatchUp` report)
  but not retried; B9's brief wires the open's settle into the loop and owns
  `Open_CatchUpLostTwiceThenHolds_...`. Open adoption takes only `_stateLock` (no write gate: nothing
  else can reach the drive before `OpenAsync` returns), matching B9's brief.
- **Recovery (B6) does not exist yet**: `CatchUpLostCount_SurvivesOperations` uses a consumer rescan
  as the operation that loses two and then fails without a block. The loop has no `WatchRequested`
  re-check between attempts (B6's).
- **At the limit** the stop state (`RefusedStartFault`, `WatchFailureMessage`) is recorded whether or
  not the watch is requested, so the drive reads `Faulted`, per spec 2.6.6.
- **No-block rescan exception**: message `"Drive X was not rescanned: <producer message>"`,
  `InnerException` the producer's exception.
- **Disposal releases the gates** after releasing snapshots (never disposes them), so a caller that
  passed its early disposed check waits and then throws `ObjectDisposedException` from its re-check.
- A publish that finds `_disposed` set throws `ObjectDisposedException`; the caller closes the
  unpublished block and restores the renamed-aside file.

## Self-review and adjacent findings

- `WaitForCatchUpAsync(X)` while X reads `Recovering` after a lost catch-up throws "not being
  watched" (no current instance): spec 2.6.4 says such a wait faults at once with X's fault. Left for
  B6, which owns `Recovering` waits.
- `AGENTS.md` still names `_cacheOnlyUnresumableCheckpointOrdinals` (lines 286, 298) and `_swapGate`
  (line 386) inside paragraphs that describe the pre-plan session model throughout; left for the docs
  tasks (D1-D3).
- `RetireCurrentSnapshotLocked`'s `?? throw ObjectDisposedException` branch is unreachable after the
  disposed checks (it was in `PublishSnapshot` before too) and uncovered.

## Primary checkout

`git -C C:\Users\mtsch\MFTLib status --short`: (empty output)

## Fix round 1

Commit `192ba67` "FileIndex open retries a lost catch-up and a rescan re-judges its restart after the attempts" (on `5d459cd`).

1. **Open runs the lost-catch-up loop (ruling B5-Q1).** `AddDriveAsync` hands its scan to
   `ScanOpenedDriveAsync` (`FileIndex.CatchUp.cs`): each attempt is adopted under `_stateLock`; after a
   lost catch-up it renames the lost block's canonical file aside, scans again at once, adopts the new
   block at the same ordinal (`AdoptOpenedDrive(..., replacing)`), closes the unpublished lost block and
   deletes its renamed file, and carries the loss forward as the report. At the limit the drive settles
   `Ready`, unresumable, count 3, watch refused (`RefusedStartFault` + `WatchFailureMessage`); `OpenAsync`
   does not throw. No fault is raised at open. A retry that produces no block keeps the lost block and
   records the producer failure on it; a first scan with no block stays blockless as before.
   `RenameAsideForRescan`/`RestoreRetiredFile` now take/return a `RetiredCanonicalFile?` shared by rescan
   and open. Tests (new file `FileIndexCatchUpLossTests.Open.cs`, not B9's names):
   `Open_CatchUpLostOnce_RetriesAndSettlesReady`, `OpenAsync_ThreeLostCatchUps_SettlesReadyAndRefusesTheWatch`;
   `CatchUpLost_LeavesNoBlockFileBehind` now uses the same `LossScriptedCache` fixture.
2. **Restart re-judged after the attempts.** `ResumeAfterFailedScanAsync` (`FileIndex.RescanRestart.cs`)
   replaces the pre-attempt decision on both failure paths: a drive that required a replacement stays as
   it was; a drive whose published block is now unresumable (a lost catch-up earlier in the same
   operation) records the refusal a start would record, when watched, and restarts nothing; otherwise
   the healthy watch restarts from its cursor. The no-block path now throws the B4-Q1
   `InvalidOperationException` with the producer failure as `InnerException`. Test:
   `Rescan_WatchedDrive_LostThenNoBlock_ThrowsProducerFailureAndRefusesTheWatch`.
3. **RED by scratch mutation (W40-R1)**, both uncommitted and reverted (`git status` clean after):
   - `BatchOnT_DoesNotWaitForUCommit`, mutation in `FileIndex.Watch.cs` `ApplyJournalEntriesCore`:
     `var writeGate = GetDriveRuntime('U').WriteGate;` (every batch takes U's gate, as an index-wide gate would). The edit was not denied this time.
     `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BatchOnT_DoesNotWaitForUCommit"`
     -> `Failed BatchOnT_DoesNotWaitForUCommit [10 s]` / `System.TimeoutException: The operation has timed out.` / `Failed: 1, Passed: 0`.
   - `StartDuringRescanOfUnresumableDrive_JudgesTheFreshBlock`, mutation in `FileIndex.WatchDrive.cs`
     `StartWatchingCoreAsync`: judge the published block's unresumable mark under `_stateLock` before
     waiting for the lifecycle gate (throw `RecordUnresumableCheckpointWatchFailureLocked(...)`).
     `dotnet test ... --filter "FullyQualifiedName~StartDuringRescanOfUnresumableDrive_JudgesTheFreshBlock"`
     -> `Failed ... [68 ms]` / `Assert.IsFalse failed. the start waits for the rescan's lifecycle gate` / `Failed: 1`.
   - New tests of this round, RED before the fix (`--filter` naming the four): 
     `Rescan_WatchedDrive_LostThenNoBlock_...`: `System.AggregateException: Drive T could not restart its watch after its rescan failed ... (Drive T cannot be watched: 1 scans in a row lost their journal catch-up ...)`;
     `Open_CatchUpLostOnce_RetriesAndSettlesReady`: `Assert.AreEqual failed. Expected:<2>. Actual:<1>.`;
     `OpenAsync_ThreeLostCatchUps_SettlesReadyAndRefusesTheWatch`: `Expected:<3>. Actual:<1>.`;
     (the refactored `CatchUpLost_LeavesNoBlockFileBehind` passed, unchanged behavior).
4. **No real-time poll.** Internal seam `FileIndex.LifecycleGatesTakenForDisposalForTest` (Disposal.cs),
   invoked once disposal holds every lifecycle gate; `Dispose_TakesLifecycleGatesThenWaitsForABatchHoldingItsWriteGate`
   awaits it with `WaitAsync(HangGuard)`, then asserts U's gates are held and disposal is not complete.

Verification:
- Targeted (`FileIndexCatchUpLossTests|FileIndexConcurrentRescanTests|FileIndexDisposalOrderTests`): `Passed! - Failed: 0, Passed: 27`.
- `--filter "FullyQualifiedName~Index"`: `Passed! - Failed: 0, Passed: 826, Skipped: 6, Total: 832`.
- `.\scripts\run-coverage.ps1 -NonInteractive`: `Total tests: 1681`, `Passed: 1675`, `Skipped: 6`, Failed 0; line 97.5%, branch 94.6%, `MFTLib.Index.FileIndex` 98.6%.
- `aislop scan . -d`: `99 / 100`, 5 warnings = the four baseline plus the ruled `JournalBrokerHost` 8-parameter warning.
- Primary checkout `git -C C:\Users\mtsch\MFTLib status --short`: (empty output).

## Fix round 2 (evidence only, no commit)

HEAD `192ba67`. Each mutation was applied uncommitted, the test project rebuilt
(`dotnet build MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64` -> `0 Error(s)`), the
literal command run from the PowerShell tool, then the file restored with `git checkout -- <file>` and
`git status --short` printed nothing. No classifier denial this round (two edits hit a transient
"no verdict" error and succeeded on retry).

### StartDuringRescanOfUnresumableDrive_JudgesTheFreshBlock
Mutation: `MFTLib/Index/FileIndex.WatchDrive.cs:99`, `StartWatchingCoreAsync`. Before: the method starts
with `if (!gateHeld) { await runtime.LifecycleGate.WaitAsync(...) }`. After: inserted ahead of it
`lock (_stateLock) { if (FindWatchableDriveBlockLocked(runtime.DriveLetter) is { } judged && _unresumableCheckpointsByOrdinal.TryGetValue(judged.DriveOrdinal, out var judgedReason)) { throw RecordUnresumableCheckpointWatchFailureLocked(runtime, judged, judgedReason); } }`
(judges the old block before waiting for the rescan's lifecycle gate).
```
dotnet test C:\Users\mtsch\MFTLib-worktrees\265-B5\MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~StartDuringRescanOfUnresumableDrive_JudgesTheFreshBlock"
  Failed StartDuringRescanOfUnresumableDrive_JudgesTheFreshBlock [60 ms]
  Error Message:
   Assert.IsFalse failed. the start waits for the rescan's lifecycle gate
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 69 ms - MFTLib.Tests.dll (net10.0)
```

### Rescan_WatchedDrive_LostThenNoBlock_ThrowsProducerFailureAndRefusesTheWatch
Mutation (restores the pre-fix, pre-attempt judgment): `MFTLib/Index/FileIndex.RescanRestart.cs:51`,
`ResumeAfterFailedScanAsync`. Before: `if (requiredReplacement || RefuseWatchOverUnresumableBlock(runtime))`.
After: `if (requiredReplacement)`.
```
dotnet test C:\Users\mtsch\MFTLib-worktrees\265-B5\MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~Rescan_WatchedDrive_LostThenNoBlock_ThrowsProducerFailureAndRefusesTheWatch"
  Failed Rescan_WatchedDrive_LostThenNoBlock_ThrowsProducerFailureAndRefusesTheWatch [63 ms]
  Error Message:
   Test method MFTLib.Tests.Index.FileIndexCatchUpLossTests.Rescan_WatchedDrive_LostThenNoBlock_ThrowsProducerFailureAndRefusesTheWatch threw exception:
System.AggregateException: Drive T could not restart its watch after its rescan failed, so its watch is stopped. (Drive T was not rescanned: the volume went away) (Drive T cannot be watched: 1 scans in a row lost their journal catch-up, so its block's journal cursor could no longer be resumed. Call FileIndex.RescanAsync for this drive before watching it.) ---> System.InvalidOperationException: Drive T was not rescanned: the volume went away ---> System.IO.IOException: the volume went away
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 72 ms - MFTLib.Tests.dll (net10.0)
```

### Open_CatchUpLostOnce_RetriesAndSettlesReady and OpenAsync_ThreeLostCatchUps_SettlesReadyAndRefusesTheWatch
Mutation (restores the pre-fix open: the first adoption records the loss and nothing retries):
`MFTLib/Index/FileIndex.CatchUp.cs:127`, `ScanOpenedDriveAsync`. Before:
`if (settled.CatchUpLoss is not { } catchUpLoss ||`. After:
`if (settled.CatchUpLoss is not { } catchUpLoss || adopted is not null ||`. One mutation, two runs:
```
dotnet test C:\Users\mtsch\MFTLib-worktrees\265-B5\MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~Open_CatchUpLostOnce_RetriesAndSettlesReady"
  Failed Open_CatchUpLostOnce_RetriesAndSettlesReady [37 ms]
  Error Message:
   Assert.AreEqual failed. Expected:<2>. Actual:<1>.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 46 ms - MFTLib.Tests.dll (net10.0)

dotnet test C:\Users\mtsch\MFTLib-worktrees\265-B5\MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~OpenAsync_ThreeLostCatchUps_SettlesReadyAndRefusesTheWatch"
  Failed OpenAsync_ThreeLostCatchUps_SettlesReadyAndRefusesTheWatch [38 ms]
  Error Message:
   Assert.AreEqual failed. Expected:<3>. Actual:<1>.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 47 ms - MFTLib.Tests.dll (net10.0)
```

### Green at HEAD
Rebuilt at `192ba67` (`0 Error(s)`), all four in one run:
```
dotnet test C:\Users\mtsch\MFTLib-worktrees\265-B5\MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~StartDuringRescanOfUnresumableDrive_JudgesTheFreshBlock|FullyQualifiedName~Rescan_WatchedDrive_LostThenNoBlock_ThrowsProducerFailureAndRefusesTheWatch|FullyQualifiedName~Open_CatchUpLostOnce_RetriesAndSettlesReady|FullyQualifiedName~OpenAsync_ThreeLostCatchUps_SettlesReadyAndRefusesTheWatch"
Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 127 ms - MFTLib.Tests.dll (net10.0)
```
`git status --short` in 265-B5: empty. Primary checkout untouched.
