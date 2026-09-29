# Task C2 re-review, fix round 1 (054fbb3..3541d3e)

Read-only review of `review-054fbb3..3541d3e.diff`, with head files read in `C:\Users\mtsch\MFTLib-worktrees\265-C2` for context. No tests were run. Line numbers are head lines unless marked "diff".

### Findings

**F1: ADDRESSED.** Checked against the owner ruling point by point:
- **The harness has no fault surface.** No public member was added. `HostPipeEnds`, `InMemoryBrokerPipes.CloseHostEnds` and `BeforeChannelTrackedForTest` are all internal. `StartInProcess` signatures are unchanged (diff 789-829).
- **A host fault reaches the test the way production sees it.** The fault closes pipes, which ends the process: `HasEnded`, `Ended` once, and `BrokerChannelLostException` on pending requests and open channels.
  - `HostFault_WithPendingRequest_FailsRequestEndsProcessOnceAndDisposesQuietly` asserts a null-drive loss, `HasEnded` and `Ended` count 1.
  - `HostFault_WithOpenScanChannel_FailsScanWithChannelLost` asserts `BrokerChannelLostException('C')`. That is the exact type, so `InvalidOperationException` is excluded. It also asserts the second request's loss and that the section was released.
- **`DisposeAsync` completes teardown and never throws a host session fault.**
  - Under the harness, `_control.DisposeAsync` awaits `hostExited`. That task is a continuation, and it faults only if `ExitHost` itself throws. The old path, which rethrew `serve`, is gone (`InMemoryDuplexStream.cs`, `HostLifetimeStream`).
  - Any control-close failure is caught and logged (`BrokerProcess.cs:96-102`). Channels are then swept and the reader is joined (`:104-105`).
- **Is anything swallowed that a production consumer needs?** No.
  - The only production catch is around closing the control pipe. By then `RequestEnd` has run and the outcome has already gone out through `Ended` and the failed requests. A close failure on a named pipe gives the consumer nothing to act on.
  - The log is a no-op unless diagnostics are enabled. That matches the ruling.
  - The doc comment overclaims "never throws". See New Breakage, Minor 1.
- **The two changed tests assert the right thing.**
  - `ControlWrite_FailsMidFrame_EndsProcessLoudly` (`BrokerProcessTests.cs`, diff 392-394) now asserts only that bounded disposal completes. That is the contract. The host's failure detail is pinned separately by the log test.
  - `Dispose_ControlCloseThrows_CompletesTeardownWithoutThrowing` (`BrokerProcessTests.Disposal.cs:64-79`) asserts three things: disposal does not throw (bounded), `Ended` has fired, which proves the reader was joined, and the scripted host's drive end reads EOF, which proves the channel sweep ran after the close failure. That is correct.
- **Host ends close on every way the session can end.**
  - `serve.ContinueWith(..., TaskContinuationOptions.ExecuteSynchronously)` has no `OnlyOn*` filter. It therefore runs on a return, a fault and a cancellation (`BrokerTestHarness.cs:53-54`).
  - `ExitHost` logs a fault or a cancellation and then calls `CloseHostEnds` (`:63-75`). Reading `session.Exception` observes the fault, so it never becomes an unobserved task exception.
  - Every host end is tracked at creation (`InMemoryBrokerPipes.cs:76`). That covers the control pair and every drive pair, including an end wrapped in `HeldWriteStream`.
- **The host faults during channel open.**
  - A connect after the exit fails with `IOException` (`InMemoryBrokerPipes.cs:43`).
  - A connect that raced the exit is still handled: an end tracked after `CloseAll` is disposed at once (`HostPipeEnds.Track`), so the client's connected end reads EOF.
  - The client's pending OpenChannel then fails through `End`, and the connection wait is linked to `_lifetime`. The reasoning is sound, but no test drives a fault during an open. See Minor 5.
- **The host exception reaches the diagnostics log on the control tag.** `HostFault_ExceptionMessageIsWrittenToDiagnosticsLog` checks this. It flushes first, and its class is `[DoNotParallelize]` (`BrokerProcessTests.cs:10`), as broker-common requires.
- **The four required tests exist, and each has recorded RED output** (report: `fix1-red-f1.log`). The RED descriptions fit the pre-fix code: a request that never ended, and a host fault that masked the test body's `InvalidTimeZoneException`.

**F2: ADDRESSED.**
- `AdvanceUntilCompleteAsync` and its real-time `Task.Delay` are deleted (diff 354-372).
- Both tests assert `BrokerLiveness.ControlReplyTimeout == 30 s` and `BrokerProcess.ControlReplyTimeout == BrokerLiveness.ControlReplyTimeout`.
- Each test waits for the timer, identified by its due time, on `TimerSignalingClock` (a `FakeTimeProvider`). It then advances to `limit - 1 tick` and asserts that neither the timer nor the outcome has fired. Last, it advances one tick and asserts both (`BrokerProcessTests.Timeouts.cs`, diff 297-352).
  - A fake clock fires timers synchronously inside `Advance`, so the "not fired" checks are deterministic.
- Every await is bounded by `HangGuard`.
- The RED check was a scratch mutation (60 s, 29 s). It is recorded in `fix1-red2.log` and matches the brief's port rule for new tests.
- The `occurrence: 2` coupling is noted in Minor 6.

**F3: ADDRESSED.** The three asks are met:
- **Harness pipe after close.** `HarnessPipe_AfterClose_ReadAndWriteThrowObjectDisposed` has recorded RED output showing `InvalidOperationException`. The fix is in `InMemoryDuplexStream.cs:16-46`.
- **Disposal mid-scan.** `Dispose_DuringScan_FailsScanWithChannelLostAndReleasesSection` asserts `BrokerChannelLostException('C')` and that the section was released.
- **Disposal mid-open.** `Dispose_DuringChannelOpen_FailsOpenWithChannelLostAndClosesPipe` covers `TrackChannel`'s disposed branch.

The two defects the implementer found:
- **(a) `ObjectDisposedException` after close.** For operations on an end the caller itself has closed, this matches a named pipe. `PipeStream` checks its handle on `Read*`, `Write*` and `Flush*`, and throws `ObjectDisposedException` ("Cannot access a closed pipe"). The client's drive reader and writer, and the control writer, all catch `ObjectDisposedException`, so harness and production now converge on the lost-channel type. One mismatch remains, and it is outside what this fix changed: a write after the PEER closed. See Minor 4.
- **(b) The `_closing` token (`BrokerDriveChannel.cs:21, 61-70, 81`).**
  - **Can it cancel a read that should complete?** Not in practice. `_closing` is cancelled only by `BrokerDriveChannel.DisposeAsync`.
    - The scan's own `await using` disposes the channel only after its reads have finished (`BrokerProcess.Scan.cs:53-67`).
    - The remaining callers are the process-disposal sweep and `OpenChannelAsync`'s failure cleanup. Both mean the channel is being torn down anyway.
    - One consequence: a terminal frame already read, while the collector waits for EOF (`Scan.cs:124`), is discarded as a lost channel if the process is disposed at that moment. Disposing the process during a scan implies that outcome, and the pre-fix code lost it too, since the pipe closed under the read. Acceptable.
    - A frame the reader has already received completes the underlying read before the cancellation registration can act, so an in-flight terminal frame is not cut off by the cancel alone.
  - **Does the caller see the lost-channel type?** Yes. The filter `_closing.IsCancellationRequested && !cancellationToken.IsCancellationRequested` converts the cancellation to `BrokerChannelLostException(DriveLetter, ...)`. A caller whose own token fired keeps `OperationCanceledException`, which the brief's scan-cancellation rule requires.
  - **The test for this defect probably no longer exercises it.** See New Breakage, Important 1.

### New Breakage in the Fix Diff

**Critical:** none.

**Important:**
1. **At head, the regression test for the `_closing` fix most likely passes without the fix.**
   - Files: `BrokerProcessTests.Disposal.cs:30-59` (`Dispose_DuringScanTheHostAbandons_...`) and `BrokerDriveChannel.cs:61-70`.
   - The RED run ("a pending read on a closed in-memory pipe never returns") predates the F1 harness change, and the report says F1 was amended afterwards.
   - At head, the test's disposal does the following, in order:
     1. `_control.DisposeAsync` awaits `hostExited`.
     2. The host abandons the channel after the grace period and `ServeAsync` returns.
     3. `ExitHost` calls `CloseHostEnds`, which disposes the host's drive end and completes the client's pending read with EOF.
     4. Only then does `CloseChannelsAsync` cancel `_closing`.
   - The scan therefore fails through the EOF branch (`Scan.cs:134-136`) with the same `BrokerChannelLostException('C')`. Deleting `_closing` would very likely leave the test green.
   - The production change therefore has no test that fails without it. The owner's rule is one regression test per bug fix, shown failing before the fix and passing after.
   - Fix: add a `ScriptedBroker` test in which the scripted host keeps its drive end open and sends nothing after the first frame. Start a read (a scan, or a raw `OpenChannelAsync` followed by `ReadAsync`), dispose the process, and assert `BrokerChannelLostException('C')` with the section released. Show it RED by a scratch removal of `_closing.Cancel()`.
   - Confidence: this is from reading, not from running. The scratch mutation settles it either way.

**Minor:**
1. **The "never throws" doc comment overclaims** (`BrokerProcess.cs:82-84`). The ruling's requirement, that `DisposeAsync` never throws a host session fault, is met. Two other paths still throw out of disposal:
   - A throwing `Ended` handler faults the reader, because `End` invokes `Ended` unguarded (`BrokerProcess.Control.cs:296`), and `await _controlReader` rethrows it. This is the previous review's Minor 5, still open.
   - A `channel.DisposeAsync` that throws inside `CloseChannelsAsync` aborts the rest of the sweep and skips the reader join.
   - Fix: either narrow the comment ("never throws because of how the broker ended") or guard both paths.
2. **Every drive-channel read now allocates a linked `CancellationTokenSource`** (`BrokerDriveChannel.cs:61`). That includes every watch-path frame once the watch tasks land. When `!cancellationToken.CanBeCanceled`, `_closing.Token` could be passed directly. This is only a cost, not a defect.
3. **Two tests break the "every await bounded" rule.**
   - `HostFault_WithPendingRequest_...` and `HostFault_WithOpenScanChannel_...` call `await broker.DisposeAsync()` with no `HangGuard` (`BrokerProcessTests.HostFault.cs:29, 58`).
   - They also use no `await using`, so a failed assertion before that line leaves the in-process host running.
4. **In-memory pipes still differ from named pipes on a write after the peer has closed.**
   - A named pipe throws `IOException` ("pipe is broken"), which the client turns into `RequestEnd` or `BrokerChannelLostException(drive)`.
   - `Pipe` returns `FlushResult.IsCompleted` once its reader has completed, and `PipeWriter.AsStream()` ignores that flag, so the write succeeds silently.
   - This is outside the fixed behavior (own-end-after-close), and today the client reaches the same type through the EOF read. Later watch tasks, which write Arm and Disarm on a live channel, may notice it. It should be handled when `InMemoryDuplexStream` is next touched.
5. **`Dispose_DuringChannelOpen_...`'s "ClosesPipe" assertion (`Disposal.cs:115`) is satisfied by the host's own teardown.** The host's channel ends once the control pipe closes, so the assertion does not prove that the client's failure cleanup disposed its end of the connection. The load-bearing assertion, `BrokerChannelLostException('C')` from `TrackChannel`, is correct. No test drives a host fault during a channel open, but the reasoning under F1 covers it.
6. **`TimerSignalingClock` matches timers by exact due time and creation order** (`Timeouts.cs:51`, `occurrence: 2`). Any future 30 s timer created before the connection bound would silently shift the target. The coupling is explained in a comment, so this is acceptable.

**The out-of-findings production change** (`BrokerProcess.Control.cs:97`, `cancellationToken.ThrowIfCancellationRequested()` after the write) is correct:
- **It matches the brief's R6 rule.** A started frame is finished under the process's bounded token, and "only a fully written request uses the late-reply discard rule when its caller stops waiting". A caller that cancelled during the write has stopped waiting, so it gets `OperationCanceledException`, and its entry stays until the reply is dropped:
  - if the reply has already arrived, `CompleteRequest` has removed the entry;
  - otherwise the entry stays and the reply is dropped later, so nothing leaks.
- **The flakiness was real nondeterminism in production code, not only in the test.** `reply.Task.WaitAsync(token)` returns an already-completed task in preference to an already-cancelled token. A caller that cancelled mid-frame therefore got the reply or `OperationCanceledException`, depending on whether the host answered before the continuation ran.
- **The change fixes the race rather than hiding it.** A cancellation requested before the frame finished is now always observed. A cancellation after the check can still lose to an arrived reply, which is benign under the late-reply rule.
- **Side effect:** a caller that both cancelled and hit a failed write now gets `OperationCanceledException` instead of `BrokerChannelLostException`. That is acceptable, because the caller asked to stop.

**`BeforeChannelTrackedForTest` is acceptable** (`BrokerProcess.Channels.cs:31, 99`).
- On the production path it costs one null-checked property read per channel open.
- It is an instance property, so it cannot leak across tests or processes. It is internal, reachable only through `InternalsVisibleTo`, and dies with the `BrokerProcess`.
- It "cannot be left armed" in any sense that matters: nothing static holds it.

### Out-of-Scope Observations
- The previous review's Minor 5 (the `Ended` handler runs synchronously in the reader, can throw into disposal, and can deadlock) is still open. The new doc comment makes it more visible.
- **Under the harness, disposal waits for the host's exit.** When a host operation ignores cancellation, that wait lasts `ControlClosedGracePeriod` on the host's `TimeProvider`. For a consumer host built with the system clock, that is a real-time wait during `await using`. This is the design as briefed ("disposal ends the host and awaits it"), but consumer docs should mention it.
- **Host operations the host abandons keep running after the harness has "exited"** and closed their pipe ends. They may then fault writing to disposed streams. A real process exit kills them. This is a known difference between an in-process harness and a process, and it is not new in this diff.
- The previous review's Minors 1, 2, 3, 4, 6, 7 and 8 were not in this round's scope and remain for the orchestrator.

### Tests
- **The report names the covering tests and the command, and gives the output** for each finding:
  - targeted filter: 98/98, three runs plus one more;
  - full `run-coverage.ps1 -NonInteractive`: 1365 total, 1359 passed, 6 skipped, 0 failed;
  - `aislop`: 99/100, same six warnings as base.
- These are unverified claims, but they are consistent with the diff.
- **New and changed tests against the rules:**
  - no `Task.Delay` on real time;
  - no elapsed-time comparison;
  - time driven by `FakeTimeProvider` (`TimerSignalingClock`);
  - ordering by `TestGate`;
  - awaits bounded, except as noted in Minor 3.

### Verdict
All findings addressed. One Important item in the fix itself should be closed: a test that fails without `_closing`.
