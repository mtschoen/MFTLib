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
