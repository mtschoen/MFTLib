# Task B6 report: automatic recovery

Status: DONE_WITH_CONCERNS (concern: the behavior change reached eight test files beyond the two
the brief names; every changed expectation is listed in the commit message and below).

Worktree `C:\Users\mtsch\MFTLib-worktrees\265-B6`, branch `task/265-B6`, base
`9725d0b8a246cd56b9a4cefa3e17fd1e5c024d65` (verified). Commit `220fbca` "A drive whose watch
faults recovers by rescanning itself".

## What was implemented

- **Queueing (WatchPump.cs `RecordPumpFault`).** A `Drive` or `Apply` fault on the current
  instance: record the fault (unchanged), `RecordCheckpointLossForFaultedDrive`, then
  `QueueRecovery` stores `RecoveryTicket(FailedInstance, FailedBlock)` on `DriveRuntime.Recovery`,
  sets `RecoveryState.Recovering` and adds the ticket's completion to `_recoveryCompletions`, all
  under `_stateLock` (skipped when disposed, the watch is no longer requested, or the instance is
  no longer the current faulted one); then `WatchFaulted(Drive or Apply)` is raised (R3: a handler
  reading `Drives` sees `Recovering`); only then `StartRecovery` runs the recovery with `Task.Run`.
  `Channel` never queues.
- **RecoveryState** (`DriveRuntime.cs`): `None`, `Recovering` (ticket queued or running; the drive
  reads `Recovering`), `RecoveredAwaitingCatchUp` (set by `RegisterStartingInstance` when a
  recovery's restart registers the new instance while the ticket is still the drive's; cleared by
  `CompleteWatchCatchUp`). A `Drive`/`Apply` fault while `RecoveredAwaitingCatchUp` queues nothing
  and is raised as `WatchFault(Recovery, X, <that fault's exception>)`; a `Channel` fault then is
  raised as `Channel` and also resets the state.
- **Recovery (`FileIndex.Recovery.cs`, new).** `RecoveryTicket` (completion with
  `RunContinuationsAsynchronously`; a cancellation source linked to disposal by
  `UnsafeRegister`, never disposed, link removed on completion, the same pattern as
  `WatchInstance`). `RunRecoveryAsync`: optional test hold, lifecycle gate (`WaitAsync(ticket
  token)`), `RevalidateRecovery` under `_stateLock` (not disposed, `WatchRequested`, ticket still
  `runtime.Recovery`, `Current` still `FailedInstance` and `Faulted`, `FailedBlock` still the
  published block), then `RescanWithGateHeldAsync(..., recovery: ticket, ticket token)`. Failures
  are classified by `RecoverWithGateHeldAsync`: success, `JournalCatchUpLostException` (the limit;
  already raised as `CatchUpLost` with `RecoveryStopped`) and cancellation or disposal report
  nothing; anything else is the failure. `EndRecovery` runs after the gate is released: under
  `_stateLock` it clears the ticket only if it is still the drive's (resetting the state to `None`
  on failure or when still `Recovering`) and decides whether the failure is reported; then it
  raises `WatchFaulted(Recovery)` with no gate and no lock held, completes the ticket (through
  `RecoveryCompletionWrapperForTest` when set), and only then removes the completion from
  `_recoveryCompletions`.
- **Rescan body** (`RescanRestart.cs`): `RescanWithGateHeldAsync` takes `RecoveryTicket?`; a
  manual rescan (null) clears the ticket first (`ClearRecoveryLocked`). The restart passes the
  ticket through `RestartRequestedWatchAsync` -> `StartWatchingCoreAsync` ->
  `StartWatchingWithGateHeldAsync` -> `RegisterStartingInstance`. The failed-scan restart path
  passes null.
- **Scan operation** (`CatchUp.cs` `RunScanOperationAsync`): takes `RecoveryTicket?`. For a
  recovery no attempt clears the checkpoint-loss report (keeps a `LiveWatch` loss, spec 2.6.5),
  and after a lost-catch-up publish below the limit it re-checks `WatchRequested` and returns the
  published attempt when a stop cleared it (the commit stands, no restart). `RecordLostCatchUp`
  also records `RetriedLostCatchUp` while retrying.
- **Superseding**: consumer `StartWatchingAsync` clears an existing ticket after taking the gate;
  `StopWatchingAsync` and disposal clear it (`ClearRecoveryLocked`); disposal
  (`StopEveryWatchForDisposalAsync`) also awaits every entry of `_recoveryCompletions`.
- **Waits (spec 2.6.4, left by B5)**: `WaitForCatchUpAsync(X)` while a scan retries a lost
  catch-up faults at once with that `JournalCatchUpLostException` (it threw "not being watched").
  While a recovery ticket is pending the current instance is still the faulted one, whose slot
  already faults with the drive's fault.
- **Status**: `GetWatchCatchUpStateLocked` reads `Recovering` for `RetryingLostCatchUp` or
  `RecoveryState.Recovering`.
- **Docs**: `WatchFaultKind.Drive/Apply/Channel/Recovery`, `WatchCatchUpState.Recovering`,
  `DriveStatus.WatchCatchUp`, `RescanAsync`, `StartWatchingAsync`, `StopWatchingAsync`,
  `WaitForCatchUpAsync` and the `DriveRuntime` lock-order remark describe recovery.
- **Thread count (S3)**: nothing to do; no index path passes a thread count.
- **Test seams (internal)**: `RecoveryBeforeLifecycleGateForTest` (`Func<char, CancellationToken,
  Task>`, the token is the ticket's, cancelled by disposal), `RecoveryCompletionWrapperForTest`,
  `TryGetRecoveryCompletionForTest`.
- **Test support**: `WatchHarness.WaitForRecoveryAsync(char)` and `RecoveryCount(char)` (the
  harness's own `WatchFaulted` handler captures the ticket completion present when each fault is
  raised); new `TestSupport/RecoveryHolds.cs` with `FileIndex.HoldEveryRecovery()` (parks every
  recovery before its gate on `Task.Delay(Infinite, token)` until disposal cancels it).

## TDD evidence

RED at the base plus behavior-free stubs of the three seams (and the harness extension):

```
dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexWatchRecoveryTests"
Failed!  - Failed:    20, Passed:     1, Skipped:     0, Total:    21
```

Per test (expected: there is no recovery on the base):
- `No recovery of drive T was queued.` (the harness saw no ticket when the fault was raised):
  DriveFault_RecoversByRescan_ReachesCaughtUp_LiveWatchLossSurvives, ApplyFault_Recovers,
  SecondFaultBeforeCaughtUp_RaisesRecovery_NoSecondRescan, RecoveringPublishedBeforeWatchFaultedRaised,
  RecoveryRescan_LosesCatchUpTwiceThenSucceeds_ReachesCaughtUp_NoRecoveryFault,
  RecoveryRescan_CatchUpLostThreeTimes_StopsWithRecoveryStopped_StaysFaulted_NoFurtherRecovery,
  CatchUpCounts_ArePerDrive_OtherDriveRecoversNormally, ManualRescanAfterCatchUpStop_RestoresWatchAndResetsCount.
- `System.TimeoutException` (no recovery producer call, hold or `Recovery` fault ever arrives):
  RecoveryScanFails_RaisesRecovery_DriveFaulted, StopDuringRecovery_CommitsWithoutRestart,
  DisposeDuringRecovery_CancelsIt, ConsumerRescanAfterRecoveryFault_RestoresWatch,
  RecoveringState_ReportedInDriveStatus, QueuedRecovery_SupersededByManualRescan_DoesNotScanAgain,
  QueuedRecovery_AfterStop_IsDropped, ObsoleteRecoveryFailure_DoesNotFaultNewerWatch,
  TwoDrivesFaultTogether_RecoverConcurrently, RecoveryTicketCompletion_ContinuationNotInline,
  StopDuringCatchUpRetry_CommitsWithoutRestartAndStopsRetrying.
- WaitForCatchUp_WhileARescanRetriesALostCatchUp_FaultsAtOnceWithTheLoss:
  `System.InvalidOperationException: Drive T is not being watched, so there is no catch-up to wait for.` (the B5 gap).
- ChannelFault_NoRecovery passed on the base (a negative case); see its mutation below.

Scratch mutations (W40-R1), each uncommitted, restored from a copy and rebuilt afterwards:

1. `FileIndex.WatchPump.cs` `RecordPumpFault`: `var recovers = fault.Kind is WatchFaultKind.Drive or WatchFaultKind.Apply;`
   -> `... or WatchFaultKind.Channel;`
   ```
   dotnet test C:\Users\mtsch\MFTLib-worktrees\265-B6\MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~ChannelFault_NoRecovery"
     Failed ChannelFault_NoRecovery [54 ms]
     Error Message:
      Assert.IsFalse failed. a channel fault queues no recovery
   Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1
   ```
2. Same build, `FileIndex.CatchUp.cs` `RunScanOperationAsync`: `if (recovery is not null && !IsWatchRequested(runtime))`
   -> `if (recovery is null && !IsWatchRequested(runtime))` (the recovery never re-checks).
   ```
   dotnet test ... --filter "FullyQualifiedName~StopDuringCatchUpRetry_CommitsWithoutRestartAndStopsRetrying"
     Failed StopDuringCatchUpRetry_CommitsWithoutRestartAndStopsRetrying [61 ms]
     Error Message:
      Assert.AreEqual failed. Expected:<1>. Actual:<2>. the retry re-checks the watch request
   Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1
   ```
3. `FileIndex.Recovery.cs`: `_completion = new(TaskCreationOptions.RunContinuationsAsynchronously)` -> `new()`.
   ```
   dotnet test ... --filter "FullyQualifiedName~RecoveryTicketCompletion_ContinuationNotInline"
     Failed RecoveryTicketCompletion_ContinuationNotInline [57 ms]
     Error Message:
      Assert.IsFalse failed. the ticket's continuation ran on the stack that completed it
   Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1
   ```
4. Two cases added after the first GREEN to cover branches the coverage report showed unexercised,
   one build with both mutations: `FileIndex.WatchDrive.cs` consumer start
   `if (recovery is null && runtime.Recovery is not null)` -> `... && runtime.Recovery is null)`
   (a start never clears the ticket), and `FileIndex.Recovery.cs` `QueueRecovery`'s guard reduced to
   `if (_disposed)` (a ticket is queued after a stop).
   ```
   dotnet test ... --filter "FullyQualifiedName~QueuedRecovery_SupersededByConsumerStart_DoesNotScan|FullyQualifiedName~StopBetweenFaultAndQueue_QueuesNoRecovery"
     Failed QueuedRecovery_SupersededByConsumerStart_DoesNotScan [57 ms]
     Error Message:
      Assert.IsFalse failed. the start clears the ticket
     Failed StopBetweenFaultAndQueue_QueuesNoRecovery [11 ms]
     Error Message:
      Assert.AreEqual failed. Expected:<0>. Actual:<1>.
   Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2
   ```
   (A restore by `Copy-Item` kept the backup's old timestamp, so the first rebuild after it did
   not recompile; the files were touched and rebuilt, and the green runs below are on the restored
   code.)

No classifier denial occurred.

GREEN:
```
dotnet test ... --filter "FullyQualifiedName~FileIndexWatchRecoveryTests"
Passed!  - Failed:     0, Passed:    21, Total:    21   (before the two added cases)
dotnet test ... --filter "FullyQualifiedName~FileIndexWatchRecoveryTests|FullyQualifiedName~FileIndexWatchRecoveryFaultTests|FullyQualifiedName~FileIndexWatchFaultTests"
Passed!  - Failed:     0, Passed:    36, Total:    36   (5 consecutive runs, before the two added cases)
dotnet test ... --filter "FullyQualifiedName~Index"
Passed!  - Failed:     0, Passed:   851, Skipped:     6, Total:   857   (3 consecutive runs, final code, plus one more after the last edit)
```

## Whole suite, coverage, aislop

`.\scripts\run-coverage.ps1 -NonInteractive` (final tree): Total tests 1778, Passed 1772,
Skipped 6, Failed 0. Line coverage 97.7%, branch 95.4% (2361 of 2474), `MFTLib.Index.FileIndex`
98.8%. Uncovered in new code: the null branch of the `(state as CancellationTokenSource)?.Cancel()`
link lambda in `RecoveryTicket` (the same idiom as `WatchInstance`, where the state is never null).

`aislop scan . -d`: `99 / 100 Healthy 0 errors 5 warnings`: the four baseline warnings
(`NativeSeamIsolationFixtures.cs:73`, `:79` AsyncFixer01; `CachedBlockDeletionOutcome.cs:8`, `:10`)
and the ruled `JournalBrokerHost` 8-parameter warning. No other finding.

## Lock order

Unchanged order: X's lifecycle gate, X's write gate, `_stateLock`. New sites:
- `QueueRecovery` (Recovery.cs): `_stateLock` only, called by the pump with no gate held.
- `StartRecovery`: no lock; `Task.Run`.
- `RunRecoveryAsync`: X's lifecycle gate (`WaitAsync` with the ticket token; the test hold is
  awaited before it, holding nothing). Inside the gate: `RevalidateRecovery` (`_stateLock` only),
  then the rescan body, which takes X's write gate (publish) and `_stateLock` sections exactly as a
  manual rescan does, and may raise `WatchFaulted(CatchUpLost)` holding only the lifecycle gate
  (B5's allowed raiser).
- `EndRecovery`: after the lifecycle gate is released; `_stateLock` sections only; raises
  `WatchFaulted(Recovery)` with no gate and no lock held, then completes the ticket outside the lock.
- `ClearRecoveryLocked` (DriveRuntime.cs): caller holds `_stateLock` (stop, disposal, manual rescan
  after its gate, consumer start after its gate).
- `RecordPumpFault`: `RecoveryState` read and reset inside the existing `Settle` `_stateLock`
  section; `CompleteWatchCatchUp`: inside its existing `_stateLock` section.
- `RunScanOperationAsync` `IsWatchRequested`: `_stateLock` only, lifecycle gate held.
- `TryGetRecoveryCompletionForTest`: `_stateLock` only.
- `StopEveryWatchForDisposalAsync`: collects `_recoveryCompletions` under `_stateLock`; awaits them
  outside it and before disposal takes any gate, so a recovery waiting for a gate is cancelled
  rather than waited on.

No gate is acquired while holding `_stateLock`.

## Existing tests changed (all named in the commit message)

- `FileIndexWatchRecoveryFaultTests.cs`: the four cases that called `RescanAsync` on the faulted
  drive now wait for the automatic recovery (the "message is set" assertion before the rescan is
  dropped as racy; the restart cursor is asserted instead); `FailedRestart_AfterAReplacedFaultedDrive_...`
  renamed `FailedRestart_OfARecoveredDrive_RaisesRecoveryAndReadsFaulted` (the restart failure
  arrives as `WatchFaulted(Recovery)`).
- `FileIndexWatchFaultTests.cs`: `ApplyFailure_IsReportedAsAnApplyFaultAndIsolatesTheDrive` now
  recovers (CatchingUp, no message, stop returns normally); three cases hold their recoveries.
- Outside the two named files (the concern): `ConsumerJournalIsolationTests`,
  `FileIndexCheckpointLossDetectionTests`, `FileIndexMidSessionCheckpointLossTests`,
  `FileIndexPerDriveWatchTests(.Lifecycle)`, `FileIndexWatchCatchUpTests`,
  `FileIndexWatchFailedRescanTests`, `FileIndexWatchRescanCheckpointLossTests`,
  `FileIndexWatchRescanTests`, `FileIndexCatchUpLossTests`. These tests inject a `Drive` or `Apply`
  fault but pin fault bookkeeping (checkpoint loss, stop rethrow, handle disposal, manual rescan of
  a faulted drive); unchanged they either read `Recovering`/`CatchingUp` or race the recovery (one,
  `WaitForCatchUpAsync_OnAnAlreadyFaultedDrive_ReturnsAFaultedTask`, hung on an unbounded await
  on the recovered instance). Each now calls `HoldEveryRecovery()`; seven assertions change
  `Faulted` to `Recovering`. No other assertion changed.

## Decisions (least surprising choice, named)

- **Second fault before `CaughtUp`**: raised once, as `WatchFault(Recovery, X, e)` where `e` is the
  restarted watch's own exception (`DriveWatchFaultException` or the apply failure); no `Drive` or
  `Apply` event is raised for it, since those kinds mean "recovery started". A `Channel` fault in
  that window is raised as `Channel`. The instance's outstanding fault is `e`, rethrown by the stop.
- **Recovery restart failure** (source throws on restart): `WatchFaulted(Recovery)` carries that
  exception; the drive reads `Faulted` through `RefusedStartFault`, message = the restart failure.
- **Recovery scan failure with no block**: the `Recovery` fault carries the rescan's
  `InvalidOperationException("Drive X was not rescanned: ...")` with the producer failure inside;
  the faulted instance stays current, the drive reads `Faulted` with the original fault's message.
- **Recovery fault raised after the gate is released**, not while holding it, so the only
  gate-holding raiser stays B5's `CatchUpLost`.
- **Stop after a failed restart / fresh start and an outstanding fault** (open owner gaps):
  untouched. A consumer start that supersedes a faulted instance (with or without a queued ticket)
  drops its outstanding fault, as before; a recovery's restart does the same.
- **WatchRequested re-check** applies to recovery only (spec 2.6.6 "A recovery re-checks"); a
  manual rescan keeps retrying after a stop, as B5 built it.
- **`WaitForCatchUpAsync` while `Recovering`**: during a ticket the faulted instance is still
  current, so the wait faults with the drive's watch fault; during a lost-catch-up retry it faults
  with the `JournalCatchUpLostException` being retried.

## Self-review and adjacent findings

- `FileIndexCatchUpLossTests.CatchUpLostCount_SurvivesOperations` still documents "Recovery arrives
  with task B6, so the operation ... is a consumer rescan here"; spec row "Count survives
  operations and watch instances" describes a recovery variant. Not in the brief's list; left.
- `AGENTS.md` still describes the pre-plan session model (D tasks), and the workspace's
  `index-port-common.md` line "There is NO automatic recovery yet" is now stale.
- Handler reentrancy is untouched (B8); no test blocks inside a handler.

## Files changed

Production: `MFTLib/Index/FileIndex.Recovery.cs` (new), `FileIndex.CatchUp.cs`,
`FileIndex.Disposal.cs`, `FileIndex.DriveRuntime.cs`, `FileIndex.Rescan.cs`,
`FileIndex.RescanRestart.cs`, `FileIndex.WatchCatchUp.cs`, `FileIndex.WatchDrive.cs`,
`FileIndex.WatchPump.cs`, `WatchFault.cs`, `WatchCatchUpState.cs`, `DriveStatus.cs` (docs).
Tests: `MFTLib.Tests/Index/FileIndexWatchRecoveryTests.cs` and `.LostCatchUp.cs` (new, 23 cases),
`TestSupport/RecoveryHolds.cs` (new), `TestSupport/WatchHarness.cs`, and the twelve existing test
files listed above. All `.cs` files CRLF.

## Primary checkout

`git -C C:\Users\mtsch\MFTLib status --short`: (empty output)

## Fix round 1

Commit `f8b39a0` "A stop that lands while recovery restarts a watch wins" (on `220fbca`).

### Finding 1: a stop lost while a restart registers its instance

What changed (`FileIndex.WatchDrive.cs`): a rescan's or recovery's restart (`restart`, the
gate-held path both share) no longer sets `WatchRequested`. `RegisterStartingInstance` checks under
`_stateLock`, atomically with the registration, that the watch is still requested and, for a
recovery, that its ticket is still `runtime.Recovery` (`IsRestartStillRequestedLocked`); if not it
registers nothing and returns null, and the start returns without invoking the source. The same
check runs in the start's first `_stateLock` step. Only a consumer's start sets `WatchRequested`.
`StartWatchingWithGateHeldAsync` was split into `PrepareStart` and `RunRegisteredStartAsync`
(aislop `Function too long` flagged it at 98 lines after the change). New internal test seam
`RestartBeforeRegistrationForTest` (awaited just before a restart registers). Lock order unchanged:
the new checks are inside existing `_stateLock` sections, lifecycle gate held.

Tests (in `FileIndexWatchRecoveryTests.cs`): `StopWhileRecoveryRegistersItsRestart_StopWins` and
`StopWhileRescanRegistersItsRestart_StopWins`. Each holds the restart just before registration on
a `TestGate`, calls `StopWatchingAsync(T)` (which returns), releases, and asserts one start only,
`NotStarted`, and that a second stop throws `InvalidOperationException` (the cleared request stands).

RED, before the fix (seam added, behavior unchanged):
```
dotnet test C:\Users\mtsch\MFTLib-worktrees\265-B6\MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~StopWhileRecoveryRegistersItsRestart_StopWins|FullyQualifiedName~StopWhileRescanRegistersItsRestart_StopWins"
  Failed StopWhileRecoveryRegistersItsRestart_StopWins [71 ms]
  Error Message:
   Assert.AreEqual failed. Expected:<1>. Actual:<2>. no watch starts after the stop returned
  Failed StopWhileRescanRegistersItsRestart_StopWins [11 ms]
  Error Message:
   Assert.AreEqual failed. Expected:<1>. Actual:<2>. no watch starts after the stop returned
Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 96 ms - MFTLib.Tests.dll (net10.0)
```

### Finding 2: literal RED commands for the two added cases

Re-run against the same scratch mutations as before (uncommitted; restored from copies, then
rebuilt): `FileIndex.WatchDrive.cs` consumer start `if (recovery is null && runtime.Recovery is not null)`
-> `if (recovery is null && runtime.Recovery is null)`; `FileIndex.Recovery.cs` `QueueRecovery` guard
reduced to `if (_disposed)`. One build, two commands:
```
dotnet test C:\Users\mtsch\MFTLib-worktrees\265-B6\MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~QueuedRecovery_SupersededByConsumerStart_DoesNotScan"
  Failed QueuedRecovery_SupersededByConsumerStart_DoesNotScan [53 ms]
  Error Message:
   Assert.IsFalse failed. the start clears the ticket
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 62 ms - MFTLib.Tests.dll (net10.0)

dotnet test C:\Users\mtsch\MFTLib-worktrees\265-B6\MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~StopBetweenFaultAndQueue_QueuesNoRecovery"
  Failed StopBetweenFaultAndQueue_QueuesNoRecovery [56 ms]
  Error Message:
   Assert.AreEqual failed. Expected:<0>. Actual:<1>.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 65 ms - MFTLib.Tests.dll (net10.0)
```

### Verification (after the last edit)

- Targeted (`FileIndexWatchRecoveryTests|FileIndexWatchRecoveryFaultTests|FileIndexWatchFaultTests`):
  `Passed! - Failed: 0, Passed: 40, Total: 40`.
- `--filter "FullyQualifiedName~Index"`: `Passed! - Failed: 0, Passed: 853, Skipped: 6, Total: 859`.
- `.\scripts\run-coverage.ps1 -NonInteractive`: `Total tests: 1780`, `Passed: 1774`, `Skipped: 6`,
  Failed 0; line 97.7%, branch 95.4% (2382 of 2496), `MFTLib.Index.FileIndex` 98.7%.
- `aislop scan . -d`: `99 / 100 Healthy 0 errors 5 warnings`: the four baseline warnings and the
  ruled `JournalBrokerHost` 8-parameter warning.
- Primary checkout `git -C C:\Users\mtsch\MFTLib status --short`: (empty output).

## Fix round 2

Commit `d0c1b37` "A stop that wins over a recovery's restart rethrows the fault it stopped" (on `f8b39a0`).

What changed (`FileIndex.WatchDrive.cs`): `PrepareStart` no longer retires a faulted current
instance. It returns that instance's `Drained` as the teardown to await (otherwise the retiring
instance's), and the instance stays `Current`. `RegisterStartingInstance` retires it
(`RetireCurrentLocked`, straight to drained) in the same `_stateLock` section that registers the
replacement, after the restart revalidation. A stop that lands in the restart window therefore
finds the faulted instance, takes its outstanding fault and rethrows it once, exactly as a stop
before the restart; only a restart that registers its replacement supersedes it (that discard is
unchanged, under the open owner gap). The same code serves a consumer start, a rescan's restart
and a recovery's restart. Lock order unchanged.

Tests: `StopWhileRecoveryRegistersItsRestart_StopWins` now asserts the stop throws
`DriveWatchFaultException` whose inner exception is the injected failure (it asserted a normal
return). New `StopWhileRescanOfAFaultedDriveRegistersItsRestart_RethrowsTheFault` (recoveries held,
drive faulted, manual rescan held before its restart registers, stop rethrows, no start, second
stop throws `InvalidOperationException`). `StopWhileRescanRegistersItsRestart_StopWins` (healthy
drive, nothing to rethrow) is unchanged. The stop-window cases and `HoldRestartBeforeRegistration`
moved to `FileIndexWatchRecoveryTests.StopWindow.cs` (the main file passed 500 lines);
`HoldRestartBeforeRegistration`'s wait is now `WaitForReleaseAsync(CancellationToken.None).WaitAsync(HangGuard)`.

RED, corrected assertions against `f8b39a0` production code:
```
dotnet test C:\Users\mtsch\MFTLib-worktrees\265-B6\MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~StopWhileRecoveryRegistersItsRestart_StopWins|FullyQualifiedName~StopWhileRescanOfAFaultedDriveRegistersItsRestart_RethrowsTheFault"
  Failed StopWhileRecoveryRegistersItsRestart_StopWins [74 ms]
  Error Message:
   Expected DriveWatchFaultException to be thrown.
   at MFTLib.Tests.Index.FileIndexWatchRecoveryTests.StopWhileRecoveryRegistersItsRestart_StopWins() in C:\Users\mtsch\MFTLib-worktrees\265-B6\MFTLib.Tests\Index\FileIndexWatchRecoveryTests.StopWindow.cs:line 29
  Failed StopWhileRescanOfAFaultedDriveRegistersItsRestart_RethrowsTheFault [14 ms]
  Error Message:
   Expected DriveWatchFaultException to be thrown.
   at MFTLib.Tests.Index.FileIndexWatchRecoveryTests.StopWhileRescanOfAFaultedDriveRegistersItsRestart_RethrowsTheFault() in C:\Users\mtsch\MFTLib-worktrees\265-B6\MFTLib.Tests\Index\FileIndexWatchRecoveryTests.StopWindow.cs:line 60
Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 103 ms - MFTLib.Tests.dll (net10.0)
```

Verification after the last edit:
- Targeted (`FileIndexWatchRecoveryTests|FileIndexWatchRecoveryFaultTests|FileIndexWatchFaultTests`): `Passed! - Failed: 0, Passed: 41, Total: 41`.
- `--filter "FullyQualifiedName~Index"` (2 runs): `Passed! - Failed: 0, Passed: 854, Skipped: 6, Total: 860`.
- `.\scripts\run-coverage.ps1 -NonInteractive`: `Total tests: 1781`, `Passed: 1775`, `Skipped: 6`, Failed 0; line 97.7%, branch 95.4% (2384 of 2498), `MFTLib.Index.FileIndex` 98.7%.
- `aislop scan . -d`: `99 / 100 Healthy 0 errors 5 warnings`, the four baseline warnings plus the ruled `JournalBrokerHost` warning.
- Primary checkout `git -C C:\Users\mtsch\MFTLib status --short`: (empty output).
