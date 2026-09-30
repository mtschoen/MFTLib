# Task FX4 report: Linux disposal-race failure

Worktree `C:\Users\mtsch\MFTLib-worktrees\265-FX4`, branch `task/265-FX4`, base 8dd64bf (verified), pushed to gitea.
Status: DONE.

Commits:

- facdae2 An operation admitted before disposal is cancelled by it, never refused
- 80d8475 Disposal checkpoint tests close over no variable their using scope disposes

## 1. Reproduction on Linux, per commit

Host llamabox. The script `~/scratch/fx4-repro.sh` built each commit in its own scratch clone
(`~/scratch/fx4/clone-<commit>`), then ran 20 times each:

- single: `FullyQualifiedName=...FileIndexDisposalRaceTests.RescanAsync_QueuedOnTheGateWhenDisposeAsyncBegins_IsCancelledInsteadOfPublishing`
- class: `FullyQualifiedName~...FileIndexDisposalRaceTests`
- whole: the managed run with exactly the platform filter `scripts/coverage-linux.sh` uses. No test was added to or
  removed from it.

Logs: `~/scratch/fx4/repro-<commit>.log`.

| Commit | What it is | single | class | whole |
|---|---|---|---|---|
| 03139d7 | f1a53db + FX3, before CB2 and CI2 | 2 of 20 failed | 0 of 20 | 0 of 20 |
| 6d4396a | 03139d7 + CI2 (merge), before CB2 | 0 of 20 | 0 of 20 | 2 of 20 failed (see below) |
| 8dd64bf | integration head | 1 of 20 failed | 2 of 20 failed | 4 of 20 failed |

Which test failed in each failing run:

- 03139d7 single runs 14 and 18: `RescanAsync_QueuedOnTheGateWhenDisposeAsyncBegins_IsCancelledInsteadOfPublishing`.
- 8dd64bf single run 18, class runs 3 and 8, whole run 20: the same test.
- Whole runs that failed on other tests, not this one:
  - 6d4396a runs 12 and 14, 8dd64bf runs 4 and 11: `ControlReplyWrite_BrokenPipe_EndsSessionAndCancelsEveryChannelWithinBound`
    ([10 s]).
  - 8dd64bf run 3: `OpenChannel_FirstRequestReadEndsNormallyAfterTimeout_StillRepliesError` ([20 s]).

Conclusion: the disposal-race failure already exists at 03139d7, before both CB2 and CI2. Neither CB2 nor CI2's
"FileIndex.Drives checks disposal under the state lock" introduced it. The 0 of 60 at 6d4396a is sampling at a rate
of a few percent. The two broker tests are separate intermittent failures in the MFTLib namespace. They appear only
in whole runs, and their durations (10 s, 20 s) point to a bound being hit under load. They are outside this task
and are reported, not changed.

## 2. The contract

- `FileIndex.RescanAsync` remarks: "`cancellationToken` is linked to the index's disposal, so disposing the index
  cancels a rescan in flight."
- `FileIndex.StartWatchingAsync` summary: "A `StopWatchingAsync` or `DisposeAsync` during the start cancels the
  source's start and fails this task with `OperationCanceledException`."
- Spec (docs/superpowers/specs/2026-09-28-per-drive-watch-channels-design.md), Disposal step 1: "Under `_stateLock`,
  set `_disposed`; then cancel the disposal token. Every pending start's cancellation source, every rescan and every
  recovery is linked to it, so they stop at their next checkpoint". Test table: "Disposal during rescans | Disposal
  cancels gated rescans, awaits every pump, and releases every gate".
- `FileIndex.DisposeAsync` remarks, for the queries: "each ends with `OperationCanceledException` or, if it had not
  started, `ObjectDisposedException`".

Decision: an operation admitted before disposal began and still running when disposal begins is cancelled, and ends
with OperationCanceledException. ObjectDisposedException is only for a call made after disposal began.
`RescanAsync_StartedAfterDisposeAsync_ThrowsInsteadOfPublishing` pins that case, and the entry checks keep it. The
existing test's assertion is correct, so no assertion was weakened.

## 3. Cause

`DisposeAsync` sets `_disposed` under `_stateLock` and only afterwards calls `_disposalCancellation.CancelAsync()`.
A rescan's token is `CreateLinkedTokenSource(cancellationToken, DisposalToken)`, and a linked source is cancelled by
a callback registered on the disposal token. `CancelAsync` runs its callbacks asynchronously. So there is a window,
from the flag being set until the linked token's callback runs, in which an admitted operation sees `_disposed ==
true` while its own token is still not cancelled.

The failing stack went through `PublishRescannedBlockAsync`. `WriteGate.WaitAsync(token)` passed, and then
`ObjectDisposedException.ThrowIf(_disposed, this)` under `_stateLock` threw ObjectDisposedException, where the
contract says cancellation. On Windows the callback almost always ran first. Linux scheduling opens the window a few
percent of the time.

## 4. Fix

`FileIndex.ThrowIfCancelledByDisposal(CancellationToken)` (FileIndex.Disposal.cs) first calls
`ThrowIfCancellationRequested` on the token, then throws `OperationCanceledException(..., DisposalToken)` while
`_disposed` is set. It replaces the flag check at every checkpoint an admitted gate-holding operation can reach after
disposal began:

| Checkpoint | Operation |
|---|---|
| FileIndex.Rescan.cs, after the lifecycle gate is taken | a queued rescan |
| FileIndex.Rescan.cs `ScanAndPublishAsync` entry | each scan attempt, including a lost catch-up retry and a recovery's scan |
| FileIndex.Publication.cs `PublishRescannedBlockAsync`, under `_stateLock` | the publish (the stack in the report) |
| FileIndex.WatchDrive.cs `StartWatchingWithGateHeldAsync` entry | a start queued on the gate, and a rescan's or recovery's restart |
| FileIndex.WatchDrive.cs `RegisterStartingInstance`, under `_stateLock` | a start or restart about to register its instance |

The entry checks of the public calls are unchanged and still answer ObjectDisposedException: `RescanAsync` before the
gate, `StartWatchingAsync`, `StopWatchingAsync`, the batched validators, the queries and `WaitForCatchUpAsync`.

New instance seam `FileIndex.DisposedFlagSetForTest` (internal `Action?`), invoked after the flag is set and before
the token is cancelled.

### Sibling paths checked (step 4)

- Recovery: `FileIndex.Recovery.cs:187` already treats any failure while `_disposed` or its ticket is cancelled as a
  cancellation (returns null). Its scan and restart now also meet the checkpoints above.
- Pump: `ApplyJournalEntriesCore` (Watch.cs:101, 137) can throw ObjectDisposedException for a batch that arrives
  after the flag. The pump turns that into a fault, and `RecordPumpFault` drops it while `_disposed`
  (WatchPump.cs:182), so nothing is recorded or raised. Unchanged.
- Open: an index is not observable before `OpenAsync` returns, so nothing can dispose it during its settle.
- `RetireCurrentSnapshotLocked`'s null-snapshot throw: it is reached only after the publish checkpoint under the same
  `_stateLock`, so it cannot follow a disposal that set the flag.

## RED and GREEN

Each new test lets the operation reach its checkpoint while the flag is set and the token is not yet cancelled. They
are ordered by signals (`DisposedFlagSetForTest`, the harness production gate, `RestartRequestedForTest`,
`RestartBeforeRegistrationForTest`, the CatchUpLost handler), not by time.

- `FileIndexDisposalRaceTests.RescanAsync_AdmittedBeforeDisposal_ReachingACheckpointBeforeTheTokenIsCancelled_IsCancelled`
- `FileIndexAdmittedOperationDisposalTests.StartWatchingAsync_QueuedOnTheGateWhenDisposalSetsItsFlag_IsCancelled`
- `FileIndexAdmittedOperationDisposalTests.RescanAsync_DisposalBeginsBeforeTheRetryAfterALostCatchUp_IsCancelled`
- `FileIndexAdmittedOperationDisposalTests.RescanAsync_DisposalBeginsAfterTheRestartDecision_IsCancelled`
- `FileIndexAdmittedOperationDisposalTests.RescanAsync_DisposalBeginsBeforeTheRestartRegisters_IsCancelled`

### Windows RED

The first test was written before the call sites changed (only the helper and seam existed). Command:
`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~MFTLib.Tests.Index.FileIndexDisposalRaceTests.RescanAsync_AdmittedBeforeDisposal_ReachingACheckpointBeforeTheTokenIsCancelled_IsCancelled"`

      Failed RescanAsync_AdmittedBeforeDisposal_ReachingACheckpointBeforeTheTokenIsCancelled_IsCancelled [95 ms]
      Error Message:
       Test method ... threw exception:
      System.ObjectDisposedException: Cannot access a disposed object.
      Object name: 'MFTLib.Index.FileIndex'.
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1

Per-checkpoint scratch mutations, not committed. The script `fx4_mutations.py` put back
`ObjectDisposedException.ThrowIf(_disposed, this);` at one checkpoint at a time, rebuilt, ran
`--filter "FullyQualifiedName~FileIndexAdmittedOperationDisposalTests|FullyQualifiedName~FileIndexDisposalRaceTests"`,
and restored the file:

    === C1 Rescan.cs after the gate
      Failed RescanAsync_AdmittedBeforeDisposal_ReachingACheckpointBeforeTheTokenIsCancelled_IsCancelled  System.ObjectDisposedException
    === C2 Rescan.cs ScanAndPublishAsync
      Failed RescanAsync_DisposalBeginsBeforeTheRetryAfterALostCatchUp_IsCancelled  System.ObjectDisposedException
    === C3 Publication.cs publish
      Failed StartWatchingAsync_QueuedOnTheGateWhenDisposalSetsItsFlag_IsCancelled  System.ObjectDisposedException
      Failed RescanAsync_AdmittedBeforeDisposal_ReachingACheckpointBeforeTheTokenIsCancelled_IsCancelled  System.ObjectDisposedException
    === C4 WatchDrive.cs start with the gate held
      Failed StartWatchingAsync_QueuedOnTheGateWhenDisposalSetsItsFlag_IsCancelled  System.ObjectDisposedException
      Failed RescanAsync_DisposalBeginsAfterTheRestartDecision_IsCancelled  System.ObjectDisposedException
    === C5 WatchDrive.cs registration
      Failed RescanAsync_DisposalBeginsBeforeTheRestartRegisters_IsCancelled  System.ObjectDisposedException
    === restored, build ok

Every checkpoint has at least one test that fails without it.

### Linux RED

Script `~/scratch/fx4-verify.sh 80d8475`, clone `~/scratch/fx4/clone-verify`, log `~/scratch/fx4/verify-80d8475.log`.
All five checkpoints were reverted to the flag check as a scratch mutation (`git diff --stat`: 3 files, 5 lines),
then restored with `git checkout -- MFTLib/Index`:

      Failed StartWatchingAsync_QueuedOnTheGateWhenDisposalSetsItsFlag_IsCancelled ... System.ObjectDisposedException
      Failed RescanAsync_DisposalBeginsBeforeTheRetryAfterALostCatchUp_IsCancelled ... System.ObjectDisposedException
      Failed RescanAsync_DisposalBeginsAfterTheRestartDecision_IsCancelled ... System.ObjectDisposedException
      Failed RescanAsync_DisposalBeginsBeforeTheRestartRegisters_IsCancelled ... System.ObjectDisposedException
      Failed RescanAsync_AdmittedBeforeDisposal_ReachingACheckpointBeforeTheTokenIsCancelled_IsCancelled ... System.ObjectDisposedException
    Failed!  - Failed:     5, Passed:     3, Skipped:     0, Total:     8

### Linux GREEN (80d8475)

- Both disposal classes 20 runs in a row: every run `Passed!  - Failed: 0, Passed: 8`, so `RESULT classes: 0 of 20
  runs failed`. This includes the originally failing test.
- Unmodified `scripts/coverage-linux.sh`: exit 0, `Passed!  - Failed: 0, Passed: 1619, Skipped: 84, Total: 1703`,
  managed `lines: 95.41% (8449/8855)`.

### Windows GREEN

- The two classes three times: `Passed!  - Failed: 0, Passed: 8` each time, at facdae2 and again at 80d8475.
- Whole suite through `run-coverage.ps1 -NonInteractive` at facdae2: Total 1970, Passed 1964, Skipped 6, Failed 0.
  80d8475 changes only test closures, and its classes were rerun three times.
- MFTLib.Index uncovered: `BlockFile.Flush.cs [71, 72, 93, 94]` and `CacheDirectory.cs [335, 337, 340, 342, 345]`.
  These are exactly the nine non-Windows lines. MFTLib.Index is 3398 / 3407.
- aislop at 80d8475: `99 / 100 Healthy 0 errors · 5 warnings`, the four baseline warnings plus the ruled
  JournalBrokerHost constructor warning. The first scan at facdae2 also flagged three jb AccessToDisposedClosure
  warnings in the new tests. They were fixed in 80d8475 (TaskCompletionSource signals, and a bound `Action` for the
  progress release).

## Concerns

- `ControlReplyWrite_BrokenPipe_EndsSessionAndCancelsEveryChannelWithinBound` failed in 4 of 60 whole Linux runs
  (6d4396a, 8dd64bf) and `OpenChannel_FirstRequestReadEndsNormallyAfterTimeout_StillRepliesError` in 1. Both are
  broker tests outside this task, and neither appeared in the 20 whole runs at 03139d7 or in the verification
  coverage run. They need their own task: the 10 s and 20 s durations suggest a wall-clock bound.

`git -C C:\Users\mtsch\MFTLib status --short`: (empty).

## Fix round 1

Base 80d8475 (verified). Answers `task-FX4-review.md`.

### A. The restart decision (Important): verified, fixed

Verified. `RestartRequestedWatchAsync` (`MFTLib/Index/FileIndex.RescanRestart.cs`, the `requested = runtime.WatchRequested && !_disposed` read) runs after the final publish of a
successful scan. A disposal that set its flag before that read made `requested` false and the method returned
`Task.CompletedTask`, so `RescanAsync` completed successfully. The contract says otherwise: the `RescanAsync` remarks
("disposing the index cancels a rescan in flight"), the spec's Disposal step 1 (every rescan "stop[s] at their next
checkpoint"), and the other side of the same read, where `RescanAsync_DisposalBeginsAfterTheRestartDecision_IsCancelled`
pins `OperationCanceledException`. Which outcome a rescan got depended on which side of one lock read disposal landed.

Change:

- `RestartRequestedWatchAsync` takes `bool scanPublished`. After a published scan it calls
  `ThrowIfCancelledByDisposal(CancellationToken.None)` under `_stateLock` before reading the request, so a disposal that
  has begun cancels the rescan whether or not the watch is still requested (disposal's own
  `StopEveryWatchForDisposalAsync` clears `WatchRequested` without the lifecycle gate, so checking the flag first keeps
  the outcome independent of that race). `CancellationToken.None`: the restart is deliberately not bounded by the
  caller's token.
- The failed-scan caller (`ResumeAfterFailedScanAsync`) passes `false`: that rescan already ends with the scan's
  failure, so disposal only skips the restart, as before (throwing there would wrap an OCE into the
  AggregateException that path builds).
- Recovery caller: `RecoverWithGateHeldAsync` passes its ticket through the same success path; the new OCE is caught by
  its existing filter `when (ticket.Cancellation.IsCancellationRequested || _disposed)` and returns null, so recovery
  still ends quietly with no `WatchFaultKind.Recovery` fault and no restart. Pinned by a new test (below).
- New instance seam `FileIndex.BeforeRestartDecisionForTest` (internal `Action<char>?`), invoked at the top of
  `RestartRequestedWatchAsync`, before the flag read.
- `RescanAsync` remarks now say the cancellation covers a rescan that has published its block and not yet restarted.

### B. Disposal comment (Minor): fixed

`ReleaseSnapshotsForDisposalAsync` summary (`FileIndex.Disposal.cs`) now says: an operation admitted before the flag
may still wait for a gate, and its checkpoint then ends it with `OperationCanceledException`
(`ThrowIfCancelledByDisposal`); a disposed gate would throw from its wait or release and hide the cancellation; a call
made after the flag never reaches a gate because its public entry throws `ObjectDisposedException`.

### Tests

In `FileIndexAdmittedOperationDisposalTests`, ordered by `BeforeRestartDecisionForTest` and `DisposedFlagSetForTest`
signals (TaskCompletionSource), every wait bounded by `HangGuard`:

- `RescanAsync_DisposalBeginsBeforeTheRestartDecision_IsCancelled`: the rescan blocks at the seam until disposal sets
  its flag; asserts OCE and that the source was started only once.
- `Recovery_DisposalBeginsBeforeTheRestartDecision_EndsWithoutAFault`: a drive fault queues a recovery, which blocks at
  the same seam until the flag is set; asserts the recovery completes, scanned once, restarted nothing, and the only
  fault is the original `Drive` one. This is a guard that the recovery behavior does not change, so it passes before
  and after the fix.

RED (seam present, fix absent). Command:
`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexAdmittedOperationDisposalTests.RescanAsync_DisposalBeginsBeforeTheRestartDecision_IsCancelled|FullyQualifiedName~FileIndexAdmittedOperationDisposalTests.Recovery_DisposalBeginsBeforeTheRestartDecision_EndsWithoutAFault"`

      Failed RescanAsync_DisposalBeginsBeforeTheRestartDecision_IsCancelled [89 ms]
      Error Message:
       Expected OperationCanceledException to be thrown.
      Stack Trace:
         at MFTLib.Tests.Index.FileIndexWatchRescanTests.ThrowsAsync[TException](Func`1 action) in ...\FileIndexWatchRescanTests.cs:line 581
       at MFTLib.Tests.Index.FileIndexAdmittedOperationDisposalTests.RescanAsync_DisposalBeginsBeforeTheRestartDecision_IsCancelled() in ...\FileIndexAdmittedOperationDisposalTests.cs:line 130
    Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2, Duration: 138 ms

Expected: the rescan returned normally because the decision read `_disposed` as a reason not to restart.
(A first draft awaited the rescan before the disposal; its failure surfaced as the harness's cache-directory delete
hitting a still-held `.lock` because disposal had not finished. The test awaits disposal first, so the RED shows the
real assertion.)

GREEN, the two disposal classes three times:
`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexDisposalRaceTests|FullyQualifiedName~FileIndexAdmittedOperationDisposalTests"`

    Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 199 ms
    Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 195 ms
    Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 204 ms

Also `--filter "FullyQualifiedName~FileIndexWatch|FullyQualifiedName~FileIndexRescan|FullyQualifiedName~Disposal"`:
`Passed!  - Failed: 0, Passed: 160`.

### Whole suite

`.\scripts\run-coverage.ps1 -NonInteractive` (log `.superpowers/fx4r1-coverage.log`): exit 0, Total tests 1972,
Passed 1966, Skipped 6, Failed 0. Line coverage 99.3%. MFTLib.Index uncovered: `BlockFile.Flush.cs [71, 72, 93, 94]`,
`CacheDirectory.cs [335, 337, 340, 342, 345]`, exactly the nine non-Windows lines.

### `_disposed` decision sweep (MFTLib/Index)

Every read of `FileIndex._disposed` that is not a throw:

- `FileIndex.RescanRestart.cs` restart decision: the finding; now throws after a published scan. The remaining
  `&& !_disposed` there serves only the failed-scan path, where the rescan already ends with the scan's failure, and the
  recovery path is filtered to a quiet drop. Correct.
- `FileIndex.Recovery.cs:89` `QueueRecovery`: runs on the pump after a fault, before any recovery exists; no admitted
  caller operation is waiting on it. Dropping is the designed "disposal never reports a watch fault". Correct.
- `FileIndex.Recovery.cs:161` `RevalidateRecovery`: recovery is internal and has no caller to cancel; disposal is a
  quiet drop by design (spec R3). Correct.
- `FileIndex.Recovery.cs:187` catch filter: turns any failure under disposal into a quiet recovery end, which is what
  keeps the new restart-decision OCE quiet. Correct.
- `FileIndex.WatchPump.cs:182` `RecordPumpFault`: pump fault after disposal began is dropped; no caller operation.
  Correct.
- `FileIndex.cs:258` `DisposeAsync` idempotence check. Correct.

`BlockFile._disposed` (BlockFile.cs, BlockFile.Flush.cs) is a separate object's mapping lifetime, only in throws. No
other boolean decision on `FileIndex._disposed` exists in MFTLib/Index.

### aislop

`aislop scan .` (log `.superpowers/fx4r1-aislop.log`): `99 / 100 Healthy 0 errors · 5 warnings`: the four baseline
warnings (NativeSeamIsolationFixtures.cs:73, :79; CachedBlockDeletionOutcome.cs:8, :10) and the ruled
JournalBrokerHost 8-parameter constructor. Nothing else.

### Commits (pushed to gitea `task/265-FX4`)

- 4da66e5 A rescan whose disposal begins after its publish, before its restart decision, is cancelled
- c913c3f Disposal gate comment describes admitted operations as cancelled at their checkpoint

Files: `MFTLib/Index/FileIndex.RescanRestart.cs`, `MFTLib/Index/FileIndex.Rescan.cs`,
`MFTLib/Index/FileIndex.Disposal.cs`, `MFTLib.Tests/Index/FileIndexAdmittedOperationDisposalTests.cs`.

### Concerns

- Behavior note: a rescan with no watch requested whose disposal begins after its publish now also ends with
  `OperationCanceledException` (its block is published but the index is being torn down). This follows the admitted
  operation contract; no existing test expected success there.
- Pre-existing, not changed: on the failed-scan path, a disposal that begins after the restart decision (checkpoints
  C4/C5) makes `ResumeAfterFailedScanAsync` wrap the scan failure and the OCE in an AggregateException.
- Linux verification of this round left to the controller as instructed.

`git -C C:\Users\mtsch\MFTLib status --short`: (empty).
