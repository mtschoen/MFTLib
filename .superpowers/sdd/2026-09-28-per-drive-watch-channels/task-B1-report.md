# Task B1 report: per-drive watch contract and FileIndex state machine

Worktree `C:\Users\mtsch\MFTLib-worktrees\265-B1`, branch `task/265-B1`, base
`a416b4443341cc76e595a51d458eab8f942a3b39` (verified before any change).

## What was implemented

Public contract (exactly as the brief lists it):
- `IIndexWatchSource.StartAsync(IndexWatchTarget, CancellationToken)` returning `IIndexDriveWatch`
  (`DriveLetter`, `ReadAsync`, `IAsyncDisposable`).
- `WatchStreamItem` (private protected ctor), `JournalBatch(Entries, JournalId, NextUsn)`,
  `DriveCaughtUp` (no drive letter), `DriveWatchFaultException(char, string, Exception?)`,
  `WatchFaultKind { Subscriber, Drive, Apply, Channel }`, `WatchFault(Kind, char DriveLetter, Exception)`.
- `FileIndex.StartWatchingAsync(char, ct)`, `StopWatchingAsync(char, ct)`, `WaitForCatchUpAsync(char, ct)`.
  The no-list forms and `DriveWatchFailure`, `WatchFaultKind.Source`, the merged stream, arm/disarm are gone.

Internal state machine (new files `FileIndex.DriveRuntime.cs`, `FileIndex.WatchDrive.cs`):
- `DriveRuntime` per configured drive (created in the `FileIndex` constructor that `OpenAsync` calls):
  `LifecycleGate`, `Current`, `Retiring`, `WatchRequested`, `NextGeneration`, plus `RefusedStartFault`
  (see decisions).
- `WatchInstance(driveLetter, generation, armedBlock, disposalToken)` with `StartCancellation`
  (linked to the disposal token by a registration removed at drain), `PumpStop`, `State`
  (`Starting, Running, Faulted, Retiring, Drained`), `SubscriberFaultAnnounced`, `OutstandingFault`,
  `CatchUp` slot, `Drained` (RunContinuationsAsynchronously, never faults).
- Start: ArgumentException, lifecycle gate, no-MFT-block / unresumable refusal, Running is a no-op,
  a Faulted current is retired, `Retiring.Drained` awaited, `Starting` instance registered as `Current`
  with `WatchRequested` set and the failure message cleared (R2), source invoked outside every lock,
  publish under `_stateLock` only if still `Current` and `Starting`; otherwise the start path disposes the
  handle and throws `OperationCanceledException`. A source throw records the message, faults the slot,
  clears `Current`, completes `Drained`, propagates.
- Stop: no gate; clears `WatchRequested`, retires `Current` (slot cancelled), takes the outstanding fault,
  cancels start and pump outside the lock, awaits `Drained` bounded by the caller token (through the
  non-inline helper), rethrows the fault once.
- Pump (`FileIndex.WatchPump.cs`): `ReadAsync(PumpStop.Token)`; batches through
  `ApplyJournalEntriesCore(X, instance, ...)`, which after `_swapGate` checks the instance is current,
  running and its `ArmedBlock` still published, and drops the batch otherwise (R1); `Changed` raised with no
  gate; subscriber fault once per instance; `DriveCaughtUp` completes the slot only for the current
  instance; ends classified Stop / Drive / Apply / Channel ("The watch for drive X ended without being
  stopped."); a fault is recorded only while current (and not disposing), then
  `RecordCheckpointLossForFaultedDrive(runtime, instance)` (now also instance-scoped), then `WatchFaulted`.
  The pump's finally disposes the handle exactly once and completes `Drained`.
- `PumpFaultSettlementWrapperForTest` wraps the settle step (record + slot fault).
  `ApplyJournalEntriesEnteredForTest` is the R1 hold seam inside `ApplyJournalEntriesCore`.
- `WaitForCatchUpAsync(X)`: waits on the current instance's slot (or faults at once with a refused start's
  fault); caller and disposal tokens reach it by registrations that cancel a
  RunContinuationsAsynchronously completion source (`AwaitQueuedAsync`), no `Task.WaitAsync`.
- Rescan (`FileIndex.Rescan.cs` + new `FileIndex.RescanRestart.cs`): lifecycle gate held entry to exit (R4),
  then `_rescanGate`; running/starting instance retired and drained before production; commit as before;
  restart through `StartWatchingCoreAsync(runtime, gateHeld: true, ...)` only if `WatchRequested`; failed
  scan keeps today's rule (healthy restarts from the old cursor, faulted/unresumable/blockless stays).
  `FileIndex.Rescan.Watch.cs` deleted, not ported.
- Disposal: `_disposed` set under `_stateLock`, disposal token cancelled, every drive's watch request
  cleared and current instance retired, every `Drained` awaited, never throws a fault; then the existing
  gate-and-release sequence.
- `WatchCatchUpState` slot shrunk to `State` + `Waiter` (`Complete`, `Cancel`, `Fault`);
  `CatchUpCoordinator` and the slot's `FaultWaiter`/registration machinery removed (only the coordinator
  used them).
- Doc comments updated in `DriveStatus.cs`, `FileIndex.Scanning.cs`, `FileIndexOptions.cs` (`WatchSource`),
  `FileIndex.cs`. `BlockSource.cs` and `DriveFailureKind.cs` had no session/rescan-watch wording to change.

Deleted production files: `ReadyOnFirstMoveWatchStream.cs`, `WatchStreamNotRunningException.cs`,
`FileIndex.WatchSession.cs`, `FileIndex.WatchStart.cs`, `FileIndex.WatchFaults.cs`, `FileIndex.Rescan.Watch.cs`.

Test doubles: `FakeIndexWatchSource` (StartAsync per call, `Starts`, `StartsFor`, `Handles`, `HandleFor`,
`HoldStart(TestGate, observeToken)`, `FailStart`, `ThrowOnSecondDispose` default true), new
`ScriptedDriveWatch` (`Publish`, `Queue`, `FailDrive`, `LoseChannel`, `End`, `FailOnCancellation`,
`DisposeCount`, `Disposed`, `ReadStarted`), `WatchHarness` (T/U/V, fake producer and source, `Changes`,
`Faults`, `WaitForFaultAsync`, production hold/failure/cursor controls). New `CacheOnlyUnresumableFixture`.

Tests: `FileIndexPerDriveWatchTests` (split into `.cs` and `.Lifecycle.cs`, 31 tests: every test the brief
names plus `RetiredInstanceFault_IsNotRecorded`, added so a mutation of the fault-recording scope check is
caught). `FileIndexCheckpointLossDetectionTests` edited in place (its `DriveWatchFailure` uses now fail the
drive through the handle; stop now rethrows `DriveWatchFaultException`). `FileIndexRescanCleanupTests`:
its three session-reclaim methods were removed (subject gone; the rescan-after-fault behavior is ported by
B3/B4 from the old rescan files); `_swapGate` reflection kept.

Deleted test files (ported by B2, B3, B4), plus the two unported ones and `ReadinessScriptedWatchSource.cs`
as the brief lists. Nothing had already been removed by A3/A5 among the files this brief lists.

## Decisions the brief left open (flagged for review)

1. `RefusedStartFault` on `DriveRuntime`: the brief says a failed start clears `Current` yet the test
   requires `WatchCatchUp == Faulted`; this field carries that state (and the unresumable refusal) with no
   instance. A catch-up wait faults with it; a stop clears it and never rethrows it (the start already threw
   it); the next start or a replacing rescan clears it.
2. A rescan does not retire a `Faulted` current instance before production (its pump already ended; it only
   awaits its `Drained`). A failed scan then leaves the drive faulted exactly as it was, and a restart after
   a replacement supersedes it. Spec observable behavior matches; representation differs from "retire
   Current" literally.
3. Restarts after a rescan use `CancellationToken.None` (bounded by stop/disposal via `StartCancellation`),
   so a cancelled rescan does not silently clear `WatchRequested`. A rescan cancelled while awaiting the old
   drain leaves the watch stopped but still requested.
4. Stop on a drive that is not watching completes (brief test `Stop_RethrowsOutstandingFaultOnce` requires the
   second stop to complete); spec section 3's `InvalidOperationException` for NotApplicable is left to B7.
5. The unresumable refusal does not set `WatchRequested` (brief order checks it before registration), so a
   rescan that fixes the block does not auto-start the watch; the consumer calls start.
6. The first subscriber fault becomes the instance's outstanding fault (if none), so stop rethrows it, as
   today's contract did.
7. A caller-cancelled start (its own token) clears `Current` and `WatchRequested` without recording a failure.
8. `RCS1194` on the public `DriveWatchFaultException` is suppressed with a justification, following
   `ClientDisconnectedException`: the standard overloads would construct an exception naming no drive.

## TDD evidence

The production code was written before the named tests (deviation from test-first; stated plainly). RED is
therefore shown by mutation: `.superpowers/mutate.py` applies one production mutation, builds, runs
`dotnet test ... --filter "FullyQualifiedName~FileIndexPerDriveWatchTests"`, and reverts (verified reverted).

| Mutation | Result |
|---|---|
| no-drain-wait (start does not await `Retiring.Drained`) | Failed 1: `RestartAfterStop_AwaitsOldDrain_BeforePublishing` |
| no-instance-check (R1 batch scope check disabled) | Failed 1: `RetiringPumpBatch_IsDroppedNotApplied` |
| no-start-cancel (stop no longer cancels the source's start) | Failed 1: `StopDuringStart_CancelsTheSourceStart` (hang guard) |
| no-rescan-gate (rescan releases the lifecycle gate before production) | Failed 1: `Rescan_HoldsLifecycleGateThroughProduction` |
| no-unpublished-dispose | Failed 2: `StartReturnsAfterStop_HandleDisposedByStartPath`, `HandleDisposedExactlyOnce_WhenStartReturnsAfterStop` |
| record-stale-fault (fault recorded when not current) | Failed 1: `RetiredInstanceFault_IsNotRecorded` (test added for this) |
| no-disposal-link (StartCancellation not linked to disposal) | survives: disposal also retires `Current` explicitly, so the link is redundant in B1 |
| wait-async (catch-up wait through `Task.WaitAsync`) | survives: the slot's waiter is already RunContinuationsAsynchronously, so `PumpFaultSettlesWaiter_ContinuationNotInline` cannot tell the two apart on the fault path; the test asserts the property but is not a regression detector for this mutation |

Logs: `265-B1\.superpowers\mutation-*.log`.

GREEN: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter
"FullyQualifiedName~FileIndexPerDriveWatchTests|FullyQualifiedName~FileIndexCheckpointLossDetectionTests|FullyQualifiedName~FileIndexRescanCleanupTests|FullyQualifiedName~NamespaceBoundaryTests|FullyQualifiedName~NativeSeamIsolationTests|FullyQualifiedName~FileIndex"`
-> `Passed! - Failed: 0, Passed: 231, Skipped: 0, Total: 231`.
Namespace boundary and native seam isolation guards are included in that filter and pass.

## Whole suite and aislop

Whole suite: `.\scripts\run-coverage.ps1 -NonInteractive` (after the aislop fixes, log
`265-B1\.superpowers\coverage2.log`): `Total tests: 1542, Passed: 1536, Skipped: 6`, 0 failed, line coverage
98.3%, exit 0. (The run before the aislop fixes had the same totals at 98.2%.) The suite shrank by the deleted
session tests, as the gate allows.

aislop (`aislop scan . -d`, local 0.16.0): `99 / 100 Healthy, 0 errors, 4 warnings` - exactly the four
baseline findings (`NativeSeamIsolationFixtures.cs:73`, `:79` AsyncFixer01; `CachedBlockDeletionOutcome.cs:8`,
`:10`). The first scan found 12 new findings (redundant doc summaries, null-forgiving operators, unused
`WatchInstance.Handle`/`Pump` fields and accessors, an unnecessary async, a captured disposed variable, and
`FileIndex.Rescan.cs` over 400 lines); all fixed: `Handle`/`Pump` removed from `WatchInstance` (the pump owns
the handle and `Drained` is what every waiter observes), `ApplyJournalEntriesCore` returns `[]` for a dropped
batch instead of null, and the rescan's watch interplay moved to `FileIndex.RescanRestart.cs`.

## Elevation

As far as I can determine, nothing I ran requests elevation. Every command, with the times from its log
file's modification stamp:
- `init.ps1` (23:54) did the restore and ran the onboarding provisioning. It does not launch anything elevated.
- MSBuild of the native project (23:54, 00:12), plus `dotnet build` of MFTLib and MFTLib.Tests: builds only.
- `mutate.py` runs (00:15 to 00:31) and the fix scripts (`fix1.py` to `fix5.py`, `drivestatus.py`,
  `rescan_rewrite.py`): they only edit text files, and `mutate.py` also runs `dotnet build` and a
  `dotnet test` filtered to `FullyQualifiedName~FileIndexPerDriveWatchTests`.
- Targeted `dotnet test` runs (00:14, 00:30, about 00:34). Their filters were combinations of
  `FileIndexPerDriveWatchTests`, `FileIndexCheckpointLossDetectionTests`, `FileIndexRescanCleanupTests`,
  `NamespaceBoundaryTests`, `NativeSeamIsolationTests` and `FullyQualifiedName~FileIndex`. None of the five
  files that carry `RequiresAdmin` (`JournalCloseCoalescingLiveTests`, `MftVolumeAdminTests`,
  `NtfsVolumeInformationAdminTests`, `UsnJournalLiveTests`, `UsnJournalSyntheticTests`) contains any of
  those names, so no admin test was selected. The FileIndex tests use fake producers and watch sources and
  launch no broker.
- `run-coverage.ps1 -NonInteractive`, twice: the first finished at 00:32, the second started at about 00:36
  and finished at 00:37. Both used `-NonInteractive`, which filters `TestCategory!=RequiresAdmin` and skips
  the script's `Start-Process -Verb RunAs` branch. Its elevation-related unit tests run through seams
  (sub-millisecond, per the log).
- `aislop scan .` (00:31, 00:33, 00:36): static analysis and builds.
- I never started TestProgram.exe or any broker.

At about 00:35 my lane was running the targeted FileIndex tests and `aislop scan . -d`, and neither of
those elevates. I cannot rule out that another lane's process on this machine raised the prompt.

## Files changed

Production (`MFTLib/Index`): new `DriveWatchFaultException.cs`, `IIndexDriveWatch.cs`,
`FileIndex.DriveRuntime.cs`, `FileIndex.WatchDrive.cs`, `FileIndex.RescanRestart.cs`; rewritten
`IIndexWatchSource.cs`, `WatchStreamItem.cs`, `JournalBatch.cs`, `WatchFault.cs`, `WatchCatchUpState.cs`,
`FileIndex.Watch.cs`, `FileIndex.WatchPump.cs`, `FileIndex.WatchCatchUp.cs`, `FileIndex.WatchTargets.cs`,
`FileIndex.Rescan.cs`; modified `FileIndex.cs`, `FileIndex.Disposal.cs`, `FileIndex.WatchCheckpointLoss.cs`,
`FileIndex.Scanning.cs`, `DriveStatus.cs`, `FileIndexOptions.cs`; six deleted as listed above.
Tests: new `Index/FileIndexPerDriveWatchTests.cs`, `Index/FileIndexPerDriveWatchTests.Lifecycle.cs`,
`Index/CacheOnlyUnresumableFixture.cs`, `TestSupport/ScriptedDriveWatch.cs`; rewritten
`TestSupport/FakeIndexWatchSource.cs`, `TestSupport/WatchHarness.cs`; edited
`Index/FileIndexCheckpointLossDetectionTests.cs`, `Index/FileIndexRescanCleanupTests.cs`; 17 deleted.

## Adjacent items noticed (not fixed)

- AGENTS.md "Checkpoint loss", "Watch start readiness", "Watch and catch-up lifetime" and the journal
  isolation paragraph still describe the session watch; spec section 4 schedules their rewrite.
- Broker client tests (`BrokerArmOrderingTests`, etc.) use only `FakeIndexWatchSource.HangGuard`, which is
  kept; they belong to the C lane.
- The rescan's token is not yet linked to the disposal token (spec 2.6.4); disposal still waits out a rescan
  through `_rescanGate` as before. Left for B5.

## Fix round 1 (commit 37bc5bb)

HEAD was confirmed at 491a88b825af17ebf11bce2b9a4bcc31201ea1cb before starting, and the lock was re-acquired.

1. **Stop on a drive that is not watching throws.**
   - Change: `StopWatchingAsync(X)` now throws `InvalidOperationException` ("Drive X is not watching, so
     there is no watch to stop.") when `!WatchRequested && Current is null && Retiring is null`. That is spec
     2.6.4's definition of watching for stop, with a retiring instance counting as an instance. The check
     runs under `_stateLock` before anything is changed. The doc comment and an `<exception>` tag were updated.
   - Decision (d) is withdrawn. Decision (a) keeps working: a start that failed at its source leaves
     `WatchRequested` set, so that drive still counts as watching and a stop clears its faulted state. An
     unresumable refusal never sets `WatchRequested`, so a stop there throws.
   - Tests: `Stop_RethrowsOutstandingFaultOnce` now asserts that the second stop throws
     `InvalidOperationException`. The new `Stop_DriveNeverStarted_ThrowsInvalidOperation` covers a drive that
     was never started.
   - RED: `Assert.ThrowsException failed. No exception thrown. InvalidOperationException exception was expected.`
     (both tests).
2. **A cancelled rescan keeps a healthy watch.**
   - Change: `RetireWatchForRescanAsync` awaits the old instance's `Drained` without the rescan's token. The
     stop request already ends the pump's read, so the drain is prompt. The cancellation is then observed by
     the scan, and the existing failed-scan rule restarts the healthy watch from its old cursor before
     `OperationCanceledException` propagates. The `RescanAsync` remarks were updated.
   - Decision (c) is unchanged; it is what makes that restart independent of the cancelled token.
     Decision (b) is unchanged.
   - Test: `Rescan_CancelledWhileTheOldWatchDrains_RestartsTheHealthyWatch` holds the old pump inside an
     apply, starts the rescan, cancels its token, and releases the pump.
   - RED: `Assert.AreEqual failed. Expected:<2>. Actual:<1>. a healthy watch resumes after a failed scan,
     cancellation included`.
3. **Cancelled waits never run inline.**
   - Test: `TokenCancelledInsideAHandler_NeverRunsTheWaitersContinuationInline`. A `Changed` handler on
     drive T sets a `[ThreadStatic]` flag, cancels the tokens of a pending `WaitForCatchUpAsync('V')` and of a
     pending `StopWatchingAsync('U')`, then clears the flag. U's pump is held inside an apply, so its stop is
     pending. Each awaiter records the flag, and both must record false.
   - The implementation was already correct, so the test passed on the committed code.
   - RED via mutation: `python .superpowers/mutate.py wait-async "FullyQualifiedName~TokenCancelledInsideAHandler"`
     gives `Failed! ... Assert.IsFalse failed. the catch-up wait's continuation ran on the cancelling
     handler's stack`, so the mutation is now killed. The mutation was reverted afterwards (`git diff` on
     `FileIndex.DriveRuntime.cs` is empty).
   - The first draft of this test was wrong: it waited on V before V was started. That was fixed before the
     mutation run. aislop then flagged captured disposed variables in the first version; the handler moved
     to a static `CancelWhileFlagged` helper and the operations are started eagerly.
4. **Restored test.** `RescanAsync_AfterTheSourceEndedWithoutAStop_ClearsTheStaleFaultedCatchUp` is back in
   `FileIndexRescanCleanupTests.cs` in per-drive form.
   - What it does: T's handle ends without a stop, which is a Channel fault, so T reads Faulted with a
     message. A rescan then restarts the still-requested watch from the fresh cursor. The test asserts
     `CatchingUp`, a null `WatchFailureMessage`, and a last start of `T 13/9000`.
   - It passes on the committed code, because it restores coverage rather than exposing a bug.

Verification:
- Targeted: `dotnet test ... --filter "FullyQualifiedName~FileIndexPerDriveWatchTests|FullyQualifiedName~FileIndexRescanCleanupTests|FullyQualifiedName~FileIndexCheckpointLossDetectionTests"`
  gives `Passed! - Failed: 0, Passed: 41`.
- After the test-only aislop fix: `Passed! - Failed: 0, Passed: 38` (the per-drive and rescan-cleanup classes).
- Whole suite: `.\scripts\run-coverage.ps1 -NonInteractive` (`265-B1\.superpowers\coverage3.log`) gives
  `Total tests: 1546, Passed: 1540, Skipped: 6`, 0 failed, 98.3% line coverage, EXIT 0. That run came before
  the final test-only refactor of the new inline-continuation test; the production code was identical.
- aislop: `aislop scan . -d` gives `99 / 100, 0 errors, 4 warnings`, only the four baseline findings.

Elevation: this round ran only builds, filtered `dotnet test` runs (no `RequiresAdmin` class matched), one
`run-coverage.ps1 -NonInteractive`, `aislop scan`, and `mutate.py`. None of these starts an elevated process.
