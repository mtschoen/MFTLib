# Task B1 re-review, fix round 1 (491a88b..37bc5bb)

Line references of the form `diff:N` are line numbers in `review-491a88b..37bc5bb.diff`. Source
references are to the 265-B1 worktree at 37bc5bb, which I read but did not change.

### Findings

**F1: ADDRESSED.**
- The new guard is at `diff:363-367`, under `_stateLock` and before any mutation. It throws
  `InvalidOperationException` when `!WatchRequested && Current is null && Retiring is null`.
- That matches spec 2.6.4 lines 445-447 ("X counts as watching for stop when `WatchRequested` is set or
  an instance exists"). An instance is `Current` or `Retiring`, and those are the only two instance slots.
- The cases I checked:
  - **Retiring instance.** It counts as watching. `RetireCurrentLocked` (`FileIndex.DriveRuntime.cs:157-175`)
    puts an undrained instance in `Retiring`. `CompleteInstanceDrain` (`:182-198`) clears it once the drain
    finishes. A stop whose token fired, followed by a second stop during teardown, therefore succeeds and
    waits. A stop after the teardown finished throws.
  - **Stop during a rescan.** The rescan has retired `Current`, but `WatchRequested` is still set, so the
    stop succeeds. This is the spec's own example.
  - **Refused start at the source.** `WatchRequested` stays set (spec 2.6.3 sets it before the source is
    invoked), so stop succeeds and clears the refusal. The doc comment says this (`diff:346-348`).
  - **Unresumable (cache-only) refusal.** `WatchRequested` is never set and no instance exists, so stop
    throws. This also resolves the first-review Minor 1 inconsistency, where a stop flipped such a drive
    from `Faulted` to `NotStarted`.
  - **Second stop after the outstanding fault was rethrown.** The first stop clears `WatchRequested`.
    `RetireCurrentLocked` sends the already-drained faulted instance straight to `Drained`, not to
    `Retiring`, so the second stop throws.
- Tests:
  - `Stop_RethrowsOutstandingFaultOnce` asserts the throw on the second stop (`diff:145-147`).
  - The new `Stop_DriveNeverStarted_ThrowsInvalidOperation` covers a drive that was never started
    (`diff:150-158`).
  - The `<exception>` tag and the doc comment were updated (`diff:346-353`).
  - The report gives RED output for both tests.

**F2: ADDRESSED.**
- `RetireWatchForRescanAsync` no longer takes the token and awaits each drain directly (`diff:288-320`).
  The cancellation then reaches `SwapDriveBlockAsync`. The `catch (Exception) when (!requiresReplacement)`
  arm (`FileIndex.RescanRestart.cs:21-25`) restarts the healthy watch through `RestartRequestedWatchAsync`,
  and `OperationCanceledException` then propagates.
- A faulted drive (`requiresReplacement`) skips that arm and stays faulted. That matches the failed-scan
  rule in spec 2.6.4 lines 478-480.
- The regression test `Rescan_CancelledWhileTheOldWatchDrains_RestartsTheHealthyWatch` (`diff:31-50`)
  exercises exactly this window:
  - The pump is held in an apply, so the rescan is parked on the drain.
  - Everything from `RescanAsync` entry to the drain await runs synchronously, because the lifecycle gate
    (`FileIndex.Rescan.cs:61`) and `_rescanGate` (`:64`) are both uncontended. The token therefore cannot
    be observed before retirement, and the test is deterministic.
  - It asserts a second start from the old cursor (`JournalId`/`NextUsn`). The held batch at USN 700 is
    dropped by the scoping rule once the instance retires, so a restart from the old cursor is correct.
  - The report gives RED output (`Expected:<2>. Actual:<1>`).
- The `RescanAsync` remarks were updated (`diff:237-239`).

**Direct answer: can the rescan now wait without bound?** Yes, in narrow cases, and the spec permits it.
- **When it can hang.** The drain is `await drain` with no token (`diff:316-320`). It never completes if
  the old pump:
  - is blocked in a `Changed`/`WatchFaulted` handler;
  - is in a source `ReadAsync` that ignores the stop token; or
  - is in a handle `DisposeAsync` that never returns (the pump's `finally` disposes the handle before it
    completes `Drained`).
- **What else bounds it: nothing in B1.**
  - Disposal does not bound it. The rescan token is not yet linked to the disposal token (the report
    leaves that to B5), and even that link would not reach an untokened await. Disposal also waits out the
    rescan through `_rescanGate`.
  - The lifecycle gate does not bound it either. The rescan holds X's gate, so a concurrent
    `StartWatchingAsync(X)` queues on it and can leave only through its own token.
  - The rescan also holds the index-wide `_rescanGate` for the whole wait. Rescans of other drives queue
    behind it and can leave only through their own tokens.
  - `StopWatchingAsync(X)` takes no gate, so stop is unaffected.
- **Why these hangs are not new.** In every case above, stop and disposal hang the same way, because both
  must await that pump's `Drained`. So the fix makes no pump teardown unbounded that was not already
  unbounded. It removes only the rescan caller's ability to give up.
- **Why the spec permits it.**
  - Spec 2.6.4 says stop "awaits `Drained` bounded by its own token" (line 442), but says only "retire
    `Current` and await its `Drained`" for rescan (line 475). No bound is required there.
  - The failed-scan rule (lines 478-480) effectively forbids leaving the drain early without a restart,
    and that was the old behavior.
  - The only hang a consumer causes directly is a handler that synchronously blocks on `RescanAsync`. Spec
    2.6.8 rejects that call at entry, and B8 implements the rejection (task-B8-brief.md).
  - Until B8 lands, such a handler deadlocks with no token escape. Before this fix it could escape by
    cancelling. That is transitional and does not block (see Minor 2 below).

**F3: ADDRESSED.**
- `TokenCancelledInsideAHandler_NeverRunsTheWaitersContinuationInline` (`diff:52-85`) works as follows:
  - A `Changed` handler on T sets the `[ThreadStatic]` `_settlingPumpFault` flag.
  - It cancels the tokens of a pending `WaitForCatchUpAsync('V')` and a pending `StopWatchingAsync('U')`.
    U's pump is held in an apply, so the stop is genuinely awaiting `Drained`.
  - It then clears the flag in `finally` (`diff:92-114`).
  - Each awaiter records the flag after its `OperationCanceledException` (`diff:80-84`), and both must
    record false (`diff:75-76`). This is the test the first review asked for, covering spec row 1339 too.
- The report shows the `wait-async` mutation is now killed, with its failure text. The mutation targets
  the shared queued-await path, so the stop path is covered through the same code. That is inference: the
  mutation run filtered only this test and its failure named the wait assertion.

**F4: ADDRESSED.**
- `RescanAsync_AfterTheSourceEndedWithoutAStop_ClearsTheStaleFaultedCatchUp` is restored in
  `FileIndexRescanCleanupTests.cs` (`diff:183-207`), in per-drive form:
  - The handle ends without a stop, and a `Channel` fault is awaited.
  - It asserts `Faulted` with a message before the rescan.
  - It asserts `Ready`, `CatchingUp` and a null `WatchFailureMessage` after the rescan, plus a last start at
    the fresh cursor 13/9000.
- That keeps the original method's intent (the rescan clears the stale faulted catch-up and the message)
  under the new model. It passes on the committed code, as expected for restored coverage.

**Test claims.**
- For each finding, the report names the covering tests, the command (a filtered `dotnet test` over the
  three classes, `mutate.py` for F3, and `run-coverage.ps1 -NonInteractive`) and the output: 41/41, then
  38/38; 1546 total with 0 failed and 98.3%; aislop 99/100 with the four baseline warnings.
- One caveat, which the report discloses itself: the whole-suite run predates the test-only refactor of
  the F3 test. The production code was identical, and the targeted 38/38 run covers the refactored test.
- The diff is consistent with every claim. I ran no tests.

### New Breakage in the Fix Diff

Critical: none.

Important: none.

Minor:
1. **The new stop guard trusts `Retiring`, and there is a narrow window that can leave `Retiring` stale**
   (`diff:363`, `FileIndex.DriveRuntime.cs:166-173`, `:182-197`).
   - The race: a faulted pump that is still `Current` runs `CompleteInstanceDrain`'s lock section, which
     does not clear `Retiring` because the instance is not retiring. It then releases the lock before
     `instance.CompleteDrain()`.
   - A stop in that gap sees `Drained.IsCompleted == false`, so it sets `Retiring = instance`.
   - Nothing clears that `Retiring` afterwards. Every later stop on the drive then passes the guard and
     completes instead of throwing `InvalidOperationException`.
   - The gap itself predates the fix, but the fix made stop's contract depend on it. A fix is to complete
     `Drained` before, or inside, the section that clears `Retiring`, or to have `RetireCurrentLocked` treat
     a `Faulted` instance whose pump has exited as drained.
2. **The drain comment overstates promptness** (`diff:312-315`). "The stop request already ends the pump's
   read, so the drain is prompt" is false while the pump is inside a subscriber handler or a handle
   `DisposeAsync`. Suggest: "prompt unless the pump is blocked in a handler (rejected for this index's
   operations by the reentrancy guard) or in the source's own teardown." Until B8 lands, a handler that
   synchronously blocks on `RescanAsync` of its own drive deadlocks with no token escape. Before this fix,
   cancelling the rescan's token let it escape.
3. **`TokenCancelledInsideAHandler_...` releases `draining` only on the success path** (`diff:77`). If an
   assertion fails, U's pump stays held until the harness is disposed. Whether that hangs the test
   depends on `WatchHarness.Dispose` (which is outside the diff), so this is noted only.

### Out-of-Scope Observations
- Disposal still waits out a rescan through `_rescanGate`, with no disposal link on the rescan token. That
  was already deferred to B5. Note for B5: linking the rescan token to disposal will not reach the drain
  wait, which is now untokened by design. B5 should state that disposal is bounded there only by the
  pump's own teardown.

### Verdict
All findings addressed
