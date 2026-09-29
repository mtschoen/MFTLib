### Findings

F1: ADDRESSED.

Fixed test: `RunBroker_SessionFails_FlushesDiagnosticsBeforeLeaving`, `MFTLib.Tests/DefaultElevatedEntryRunnerTests.cs:213-266`. The body is now in `try` (240-259), and `finally` (260-265) calls `sink.Release()` and then `EnsureRunnerLeavesAsync(runTask, server)`. The cleanup at :20-35 keeps the fake exit seam whenever `_runnerStillServing` is set.

Exit paths:
- Normal pass. The body releases the sink at :251 and the runner faults with InvalidDataException, which the test asserts at :252. The `finally` releases the sink again (no effect, and the pass path ran green in the implementer's runs), disposes the server, and the helper finds the task already completed. `_runnerStillServing` stays false, so cleanup calls `ResetToDefaults`, restoring the real `Environment.Exit`. The runner has already left, so it cannot reach the real exit.
- Assertion throws before the gate is opened (for example :250, or a timeout of `sink.Entered` or `FlushBoundStarted` at :247-248). The `finally` opens the gate (:263), so the writer drains the queued lines. The flush wait is not ended by the fake clock, so it needs the drain, and the sink's own wait is capped by `HangGuard` at :227. The server is disposed, which ends the session, and the runner leaves within `HangGuard`. The flag stays false and the exit seam is reset only after the runner has left. If the runner instead calls the exit seam, that seam is still the fake because cleanup has not run yet.
- A bounded wait times out (:242 connect, :252 run task). The `finally` releases the sink, disposes the pipe, then waits up to `HangGuard` for the runner (:313-316). If the runner has still not left, the helper sets `_runnerStillServing` (:319) and rethrows. Cleanup then installs `_ => { }` (:25) instead of resetting, so the lingering thread cannot kill the test host. If the runner does leave, the reset is safe. In both cases the thread cannot reach the real exit.
- Sink gate: released on every exit path (:263). Also `TestGate.Release` twice on the pass path did not throw in the implementer's three green class runs.

Other tests in the class that start a runner (audited against the same rule):
- `RunBroker_NullPipeName` (:39-47): synchronous, seam faked first, no thread left behind. OK.
- `RunBroker_ValidControlPipe` (:51-78): helper in `finally`. OK.
- `RunBroker_ClientDisconnectsDuringWatch` (:82-150): helper in `finally`, mock released in `finally` and by the token registration. OK.
- `RunBroker_ClientDisconnectsDuringArmAndScan` (:154-209): the same shape. OK.

Faulted-counts-as-left change (`EnsureRunnerLeavesAsync`, :311-315): correct for every user of the helper. `runTask.ContinueWith(static _ => { }, TaskScheduler.Default)` completes on completion, fault or cancellation, so `left.WaitAsync(HangGuard)` times out only when the runner thread is still running, which is the only condition that matters for the seam. It cannot hide a wrong-reason fault in a test that expects a clean exit, because each such test awaits `runTask.WaitAsync(cts.Token)` inside the `try` (:70, :141, :200), which rethrows the fault before `Assert.AreEqual(0, exitCode)`. The flush test asserts the exact exception type at :252. The one change in behavior is when the body has already thrown: the helper no longer replaces that exception with the runner's fault, so the original failure is the one reported.

Tests and command: the report names the covering class (5/5, three runs) and `run-coverage.ps1 -NonInteractive` (1426 total, 1420 passed, 6 skipped, 0 failed, 97.6% line coverage). I did not re-run these. No test induces the failure exit paths above, so the failure-path reasoning rests on reading the code, not on a failing run.

### New Breakage in the Fix Diff

Critical: none.
Important: none.
Minor:
- `DefaultElevatedEntryRunnerTests.cs:317-320`: if the helper itself throws `TimeoutException` from the `finally`, it replaces the body's original exception, so an assertion failure that led to it is not shown. The seam-safety behavior is correct; only the failure message is affected.

### Out-of-Scope Observations

- The report mentions an amended commit and an "intermediate scan" finding about `await using`; the fix diff's context already shows `await using var server`, which is consistent. Nothing to act on.

### Verdict

All findings addressed
