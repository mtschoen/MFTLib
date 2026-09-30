# Task FX5 report: two broker tests fail intermittently in whole Linux runs

Worktree `C:\Users\mtsch\MFTLib-worktrees\265-FX5`, branch `task/265-FX5`, base 8dd64bf (verified), pushed to `gitea`.
Status: DONE.

Commits (test-only; no production file changed):

- 91ed02b First-request timeout tests hold the connection until the host's connect bound exists
- b5c4557 Channel tests' scripted watch reports cancellation even when its own read ends it first

Verdict: both are TEST defects, with two different causes. Neither is a wall-clock bound being hit
under load: both waits were for signals that, on the failing interleaving, never arrive at all.

## 1. Reproduction (llamabox, clone `~/scratch/fx5/clone`, scripts `~/scratch/fx5/loop.sh` and `stress.sh`)

"Whole" is the managed run with exactly the platform filter `scripts/coverage-linux.sh` uses (copied from
`~/scratch/fx4-repro.sh`). The suite has no assembly-level `[Parallelize]`, so a whole run executes
one test at a time. Logs and the trx of every failing run: `~/scratch/fx5/<label>/`.

| Commit | Run | Runs | Failed runs | Which test, where |
|---|---|---|---|---|
| 8dd64bf | whole (`whole-base`) | 20 | 1 | run 11: `ControlReplyWrite_BrokenPipe_...` [10 s], `TimeoutException` at `JournalBrokerHostChannelTests.Watch.cs:154` |
| 8dd64bf + wait diagnostic | whole (`whole-diag`) | 20 | 0 | |
| 8dd64bf | class `JournalBrokerHostChannelTests`, 32 busy-loop hogs (`stress-class`) | 60 | 1 | run 48: `WatchChannel_ClientClosesPipe_EndsQuietly_OtherChannelUnaffected` [10 s] at `Watch.cs:93` |
| 8dd64bf + diagnostic | class, 32 hogs (`stress-class-diag`) | 150 | 1 | run 109: `ControlEof_CancelsEveryChannelAndReturnsWithinGracePeriod` [10 s] at `Watch.cs:120` |
| b5c4557 | whole (`whole-fixed`) | 20 | 0 | |
| b5c4557 | class, 32 hogs (`stress-class-fixed`) | 210 | 0 | |

FX4's earlier counts (whole, 20 runs each): test 1 failed 2 of 20 at 6d4396a and 2 of 20 at 8dd64bf; test 2 1 of 20 at
8dd64bf. Test-alone loops were not run: neither test has ever failed alone, and each mechanism below is made
deterministic by a scratch mutation instead. `git log 03139d7..6d4396a` touches no broker code or broker test,
so FX4's 0 of 20 at 03139d7 is sampling at a rate of a few percent.

Load was not the trigger for test 1 in the sense the dispatch hypothesized: with 32 CPU hogs (load average 55) the
class alone failed 2 in 210 runs, about the whole-run rate, and no other class runs concurrently with it in any
case. Load only widens a scheduling window that exists on every run (section 3).

## 2. Test 2: `OpenChannel_FirstRequestReadEndsNormallyAfterTimeout_StillRepliesError` (20 s)

### Cause

`JournalBrokerHost.ConnectChannelAsync` (`MFTLib/Broker/Host/JournalBrokerHost.Channel.cs:50-53`) starts the
connector with `Task.Run` and then calls `connection.WaitAsync(ChannelConnectTimeout, _timeProvider, session.Token)`.
`Task.WaitAsync` returns at once, creating no timer, when the task has already completed. The harness's in-memory
connector completes immediately, so if the `Task.Run` item finishes before the host thread reaches `WaitAsync`
(the host thread descheduled between the two lines), no connect-bound timer exists. The test assumed
(`JournalBrokerHostChannelTests.ChannelLifetime.cs:110`, comment "The channel's connect bound was the first 30 second
timer; its first-request bound is the second") that the first-request bound is 30 second timer number 2. On that
interleaving it is number 1, `TimerCreated(FirstRequestTimeout, 2)` never completes, the test times out after 10 s,
and `HostChannelHarness.DisposeAsync` then waits another 10 s for a session whose channel is parked on the
uncancellable read nobody released: 20 s, exactly the observed duration.

Same defect, same class: `OpenChannel_NoFirstRequest_ChannelEndsAfterTimeout` (`JournalBrokerHostChannelTests.cs:160`),
which would fail at 10 s.

### Decision: test defect

The contract is only that a connection is bounded by `ChannelConnectTimeout` and a first request by
`FirstRequestTimeout` (`JournalBrokerHost.cs:17-21`, the `ConnectChannelAsync` / `ReadFirstRequestAsync` comments).
Nothing promises that an already-completed connection starts a timer, and not starting one is correct. The test
raced its own observation.

### Fix (91ed02b)

`HostChannelHarness` takes an optional `connectionsReleased` task; its default connector holds each connection until
it completes. New helper `OpenChannelAfterConnectBoundAsync` (`JournalBrokerHostChannelTests.cs`) opens the channel,
waits for 30 second timer 1 (necessarily the connect bound, since the connection is pending), then releases the
connection, so the first-request bound is timer 2 on every run. Both tests use it; their assertions are unchanged.

### RED / GREEN (Windows, scratch mutation per rulings W40-R1 and W40-R1a, never committed)

Mutation: `Thread.Sleep(200); // FX5 SCRATCH MUTATION` inserted after the `Task.Run` in `ConnectChannelAsync`, forcing
the rare interleaving. Command:
`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostChannelTests.OpenChannel_FirstRequestReadEndsNormallyAfterTimeout_StillRepliesError|FullyQualifiedName~JournalBrokerHostChannelTests.OpenChannel_NoFirstRequest_ChannelEndsAfterTimeout"`

RED at 8dd64bf plus the mutation:

      Failed OpenChannel_FirstRequestReadEndsNormallyAfterTimeout_StillRepliesError [20 s]
    System.TimeoutException: The operation has timed out.
          at MFTLib.Tests.TestSupport.HostChannelHarness.DisposeAsync() in ...\HostChannelHarness.cs:line 163
      Failed OpenChannel_NoFirstRequest_ChannelEndsAfterTimeout [10 s]
    System.TimeoutException: The operation has timed out.
          at MFTLib.Tests.JournalBrokerHostChannelTests.AdvanceWhenTimerExistsAsync(...) in ...\JournalBrokerHostChannelTests.cs:line 289
    Total tests: 2  Failed: 2

GREEN, fixed tests plus the same mutation: `Passed ... [246 ms]`, `Passed ... [217 ms]`, `Total tests: 2 Passed: 2`.
Mutation reverted (`git checkout -- MFTLib/Broker/Host/JournalBrokerHost.Channel.cs`, status clean) and rebuilt.

### Sweep (every `TimerCreated(..., occurrence)` in `MFTLib.Tests`)

| Site | Result |
|---|---|
| `JournalBrokerHostChannelTests.cs:136` `ConnectorNeverConnects` (`ChannelConnectTimeout`, 1) | ordered: the connection never completes, so the timer always exists |
| `JournalBrokerHostChannelTests.cs:160` `NoFirstRequest` | defect, fixed in 91ed02b |
| `JournalBrokerHostChannelTests.ChannelLifetime.cs:111` | the failing test, fixed in 91ed02b |
| `JournalBrokerHostChannelTests.Watch.cs:125,157`, `.ChannelLifetime.cs:159`, `BrokerProcessTests.Disposal.cs:51` (`ControlClosedGracePeriod`, 2) | ordered: the heartbeat sender's timer is created synchronously in the session constructor, and the drain's `Task.Delay` always creates one |
| `BrokerProcessTests.Timeouts.cs:46,74,76`, `BrokerProcessLivenessTests.cs:190` (client `ControlReplyTimeout`, 2 to 4) | ordered: the write bound is a `CancellationTokenSource` timer (always created), and the reply and connection waits are on tasks the scripted broker completes only after the test has observed that timer |

## 3. Test 1: `ControlReplyWrite_BrokenPipe_EndsSessionAndCancelsEveryChannelWithinBound` (10 s)

### Evidence

The captured failure (`whole-base` run 11) is `TimeoutException` at `Watch.cs:154`,
`await Task.WhenAll(stubborn.Cancelled, cooperative.Cancelled).WaitAsync(HangGuard)`, after line 153 (the host's
failed control write) had passed. The failing whole run took 28 s against 18 s, and the test's `finally` then
disposed the harness within 7 ms. The two other failures the stressor produced are the same wait in sibling tests:
`Watch.cs:93` (`sources["C"].Cancelled` after the client closes C's pipe) and `Watch.cs:120`
(`cooperative.Cancelled` after control EOF). Every one of the four observed failures of this family awaits the
`Cancelled` signal of a cooperative (token-observing) `ScriptedWatch`.

### Cause

`ScriptedWatch.RunAsync` (`JournalBrokerHostChannelTests.Watch.cs:184-200` at 8dd64bf):

    await using var registration = cancellationToken.Register(() => _cancelled.TrySetResult());
    ...
    await foreach (var cursor in _batches.Reader.ReadAllAsync(cancellationToken))

The channel reader registers its own callback on the same token later. `CancellationTokenSource` runs callbacks
last-registered first (`CancelAsync` runs them on one thread-pool work item; a linked source's callback cancels the
child synchronously), so the reader's callback runs first and completes the pending read with
`OperationCanceledException` on another thread. That continuation ends the iterator and disposes `registration`.
A registration disposed before its callback has started is removed and never runs. When the iterator's thread
wins that race against the callback thread (a window of a few instructions, wider under load and Linux scheduling),
`Cancelled` is never set, even though the token was cancelled and the source did stop. That is a lost signal, not a
late one; the session itself behaved correctly. `ScriptedWatchBrokerHarness.ReadAsync`
(`TestSupport/ScriptedWatchBrokerHarness.cs:282-289`) already carries the fix for exactly this race, with the same
explanation; this second, older copy of the pattern never received it.

### Decision: test defect

The spec's test table and the `ServeAsync` summary promise that ending the session (control EOF or a control reply
that finds the pipe gone) cancels every channel, and that a closed drive pipe cancels that channel. The host does
cancel the token on every run; what failed was the test double's report of it. Production is unchanged.

### Fix (b5c4557)

The cooperative path runs inside `try`/`finally`; the `finally` sets `_cancelled` when the token is cancelled, the
idiom `ScriptedWatchBrokerHarness` uses. The registration stays for the stubborn path, which never ends on its own.
The assertion is as strong as before: `Cancelled` still completes only when the source's token was cancelled.

### RED / GREEN (Windows, scratch mutation, never committed)

Mutation: in the cooperative path, `cancellationToken.Register(() => Thread.Sleep(200)); // FX5 SCRATCH MUTATION`
just before the `await foreach`, so a callback sits between the reader's and `_cancelled`'s and holds the callback
thread while the reader's continuation disposes the registration. Command:
`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostChannelTests" --logger "console;verbosity=normal"`

RED at 8dd64bf's `ScriptedWatch` plus the mutation:

      Failed ControlEof_CancelsEveryChannelAndReturnsWithinGracePeriod [10 s]
          at ...ControlEof_CancelsEveryChannelAndReturnsWithinGracePeriod() in ...\JournalBrokerHostChannelTests.Watch.cs:line 120
      Failed ControlReplyWrite_BrokenPipe_EndsSessionAndCancelsEveryChannelWithinBound [10 s]
          at ...ControlReplyWrite_BrokenPipe_EndsSessionAndCancelsEveryChannelWithinBound() in ...\JournalBrokerHostChannelTests.Watch.cs:line 154
    Total tests: 46  Passed: 44  Failed: 2

These are the exact lines and duration of the Linux failures. GREEN, the fix plus the same mutation:
`Passed!  - Failed: 0, Passed: 46, Total: 46`. Mutation line removed before commit.
`WatchChannel_ClientClosesPipe_...` passed under this mutation on Windows; it failed on Linux under load
(`stress-class` run 48) at the same kind of wait on a cooperative `ScriptedWatch`, and the fix covers it.

### Same cause for both tests?

No. Test 2 is a timer-ordinal assumption broken by `Task.WaitAsync` skipping its timer; test 1 is a lost
cancellation signal in a test double. They share only the symptom (a hang guard expiring).

### Sweep (every `Register(` in `MFTLib.Tests`)

| Site | Result |
|---|---|
| `JournalBrokerHostChannelTests.Watch.cs:187` `ScriptedWatch` | defect, fixed in b5c4557 |
| `TestSupport/ScriptedWatchBrokerHarness.cs:258` | already fixed (its `finally`) |
| `JournalBrokerHostTests.Watch.cs:20` | safe: the registration is never disposed, so nothing can remove it before it runs |
| `JournalBrokerHostChannelTests.ChannelLifetime.cs:194` `UncancellableReadStream` | safe: the read does not observe the token, so the method cannot end, and dispose the registration, before release |
| `DefaultElevatedEntryRunnerTests.cs:116,178`, `JournalBrokerHostRealSeamsTests.Operations.cs:416` | safe: test-scope timeout releases, disposed at test end |
| `Index/FileIndexBlockReleaseTests.cs:310,343`, `ParseThreadAllocatorTests.cs:37` | safe: deliberate callback-order tests, disposed at test end |

## 3a. The timer-ordinal hypothesis (team lead, C2 rereview r1 "TimerSignalingClock occurrence coupling")

- Test 2: confirmed in a specific form. The ordinal did not change because timers were reordered or a heartbeat
  timer re-armed; it changed because a timer was sometimes never created (`Task.WaitAsync` on a completed task).
  The connect bound and the first-request bound share the 30 second due time, so "wait for a timer with this due
  time" cannot tell them apart, and the clock has no owner to key on without a production seam. The fix therefore
  makes the ordinal a consequence of the test's own signals: the connection is held until the connect bound exists,
  so timer 1 is necessarily the connect bound and timer 2 the first-request bound.
- Test 1: refuted. It fails at `Watch.cs:154`, before `AdvanceWhenTimerExistsAsync` is ever called, and its
  ordinal is deterministic: the heartbeat sender's timer is created synchronously in the `ControlSession`
  constructor, before `ServeAsync` first yields, and is periodic (`FakeTimeProvider` re-arms it without another
  `CreateTimer`, so it is never counted twice); the drain's `Task.Delay` always creates the second.
- Every caller of `AdvanceWhenTimerExistsAsync` and every other ordinal use of the clock is in the timer sweep of
  section 2.

## 4. Verification

### Linux (llamabox, clone checked out detached at b5c4557, `git clean -ffxd`, `./init.sh --build`)

- 20 whole runs (`~/scratch/fx5/whole-fixed/summary.log`): `RESULT whole-fixed: 0 of 20 runs failed`, every run
  `Passed!  - Failed: 0, Passed: 1614, Skipped: 84, Total: 1698`. No other test failed.
- Unmodified `scripts/coverage-linux.sh` (`~/scratch/fx5/verify-coverage.log`): exit 0,
  `=== 20 passed, 0 failed ===`, `Passed!  - Failed: 0, Passed: 1614, Skipped: 84, Total: 1698`,
  native lines 74.5% (1032 of 1385), branches 49.0% (504 of 1028); managed lines 95.41% (8441/8847),
  branches 93.87% (2269/2417).

### Windows (worktree)

- `.\init.ps1 -Build`: EXIT=0, solution built Release|x64.
- Touched class three times at b5c4557:
  `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostChannelTests"`
  -> `Passed!  - Failed: 0, Passed: 46, Skipped: 0, Total: 46` x3 (also x3 at 91ed02b). Test build: 0 warnings, 0 errors.
- `.\scripts\run-coverage.ps1 -NonInteractive` (`.superpowers\run-coverage.log`): EXIT=0, Total tests 1965,
  Passed 1959, Skipped 6, Failed 0; line 99.3% (6906 of 6954), branch 97.5% (2531 of 2594). In `MFTLib` and
  `MFTLib.Index` only `BlockFile` (97.9%) and `CacheDirectory` (95.7%) are below 100%, the classes holding the nine
  non-Windows lines FX4 recorded. No production line changed, so Ruling COVERAGE is untouched.
- `aislop scan .` (`.superpowers\aislop.log`): `99 / 100 Healthy 0 errors · 5 warnings · 0 fixable`:
  `NativeSeamIsolationFixtures.cs:73`, `:79` (AsyncFixer01), `CachedBlockDeletionOutcome.cs:8`, `:10` (redundant doc
  comment), and `JournalBrokerHost.cs:46` (8 parameters, ruling W2-2). Nothing else.

## 5. Files changed

- `MFTLib.Tests/TestSupport/HostChannelHarness.cs` (connection hold)
- `MFTLib.Tests/JournalBrokerHostChannelTests.cs` (helper, `NoFirstRequest`)
- `MFTLib.Tests/JournalBrokerHostChannelTests.ChannelLifetime.cs` (`FirstRequestReadEndsNormallyAfterTimeout`)
- `MFTLib.Tests/JournalBrokerHostChannelTests.Watch.cs` (`ScriptedWatch`)

All four are CRLF and stay CRLF.

## 6. Supplementary Linux stress on the fix

`~/scratch/fx5/stress.sh stress-class-fixed 210 32 "FullyQualifiedName~MFTLib.Tests.JournalBrokerHostChannelTests"`
at b5c4557: `RESULT stress-class-fixed: 0 of 210 runs failed`, against 2 of 210 at 8dd64bf under the same stressor
(`stress-class` plus `stress-class-diag`). This is supporting evidence only; the deterministic proof is the
mutation RED and GREEN above.

## Concerns and adjacent notes

- No production defect found; nothing in `MFTLib` changed.
- The two test doubles `ScriptedWatch` and `ScriptedWatchBrokerHarness` duplicate the same source pattern; merging
  them is outside this task.
- The llamabox scratch clone is left detached at b5c4557, clean. Tools installed only under `~/scratch/fx5/tools`.

`git -C C:\Users\mtsch\MFTLib status --short`: (empty output). Worktree `status --short` also empty; branch
`task/265-FX5` at b5c4557.
