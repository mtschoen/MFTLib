### Findings

F1: ADDRESSED.
- The fixed 500 ms `Task.Delay` and the `_diagnosticsFlushTimeout` knob are gone (diff, `DefaultElevatedEntryRunner.cs` `-_diagnosticsFlushTimeout` and `ResetToDefaults` line removed; test file has no `Task.Delay` and no elapsed-time comparison).
- Ordering is proven by signals: `TestGate` (`sink.Entered`) shows the writer is parked with a line accepted but not processed, so the flush cannot complete (`BrokerDiagnosticsWriter.FlushAsync`, lines 71-87, waits on `_accepted > _processed`). `clock.FlushBoundStarted` is completed only by `CreateTimer` on the injected clock, and the only code that creates a timer on it is `FlushDiagnostics` (`Task.Delay(..., _timeProvider, ...)`). The flush's bound is read from the injected `TimeProvider`; the `FakeTimeProvider` is never advanced, so the bound cannot end the wait.
- Scratch mutation reported (report lines 360-368): the `FlushDiagnostics()` call removed gives a failing test (`TaskCanceledException`, FlushBoundStarted never fired), restored, then 3 passed. The report names the test, the mutation, the red output and the green count. The report does not print the exact test command line, only the outcomes (Minor documentation gap, not blocking).

Question asked about the runner's exit path:
- No time limit in the runner's exit path reads real time. `RunBroker`'s only exit-path wait is `FlushDiagnostics`, and it uses `_timeProvider`. The host built by `JournalBrokerHost.CreateDefault()` (line 43) runs on the system clock, but that is the session, not the exit path, and it is not a time limit the flush depends on. No other `Task.Delay`/`TimeProvider` use exists under `MFTLib/Broker` on the runner's path (grep).
- The "runner has not returned" check: the check is not the load-bearing proof by itself, and that is fine. It cannot be reached when the flush is skipped, because `await clock.FlushBoundStarted.WaitAsync(cts.Token)` fails first (the mutation shows this). With the flush present, the runner is parked on a pending flush with an untouched clock, so `IsCompleted == false` is deterministic. The trailing `appended` assertion additionally catches a flush that starts a bound but does not wait.
- Hang safety: every await is bounded: `server.WaitForConnectionAsync(cts.Token)`, `WriteAsync`/`FlushAsync(cts.Token)`, `sink.Entered.WaitAsync(cts.Token)`, `FlushBoundStarted.WaitAsync(cts.Token)`, `runTask.WaitAsync(cts.Token)`, and the sink's own release wait is `WaitAsync(HangGuard)`. `cts` is 10 s `HangGuard`, a hang guard on real time, not an assertion on elapsed time.

### New Breakage in the Fix Diff

Critical: none.
Important: none.
Minor:
- `DefaultElevatedEntryRunnerTests.cs` (the `Assert.IsFalse(runTask.IsCompleted, ...)` line, before `sink.Release()`): if that assertion ever fails, the exception leaves the sink parked on the gate with the writer thread blocked until the 10 s `HangGuard` expires and `runTask` is never observed. It cannot hang the suite (bounded), but it leaks a blocked thread for up to 10 s. Releasing the gate in a `finally` would avoid this. Not blocking.
- `DefaultElevatedEntryRunner.cs` `FlushDiagnostics`: `Task.WhenAny(...).GetAwaiter().GetResult()` no longer surfaces a faulted flush, where the old `.Wait(timeout)` would have thrown. The flush task only faults by cancellation (token is `None`) so this is behavior-neutral today; the comment already states the outcome is not inspected.

### Out-of-Scope Observations

None.

### Verdict

All findings addressed
