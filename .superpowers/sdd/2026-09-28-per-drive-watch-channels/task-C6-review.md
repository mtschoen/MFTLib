### Plan Compliance

- Issues found:
  - W40-R1 is not satisfied. The report gives one command template containing the literal placeholder `<Test>` and only abbreviated result summaries, rather than the exact literal command and real failing output for each required new test (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-C6-report.md:69`, `:71`, `:75`).
  - `TimedOutStop_ThenNewWatch_RunsUndisturbed` does not prove the required timed-out-stop precondition. It passes an already-cancelled token and accepts either cancellation or normal completion because the `OperationCanceledException` is optional (`MFTLib.Tests/BrokerIndexWatchSourceTests.cs:229`, `:233`, `:235`).
  - The inherited test rule forbidding real-time polling sleeps is violated by the fixed 25 ms delay in the GC polling loop (`MFTLib.Tests/Index/WatchFailureObservationTests.cs:169`).
  - Standard verification was not run on the final tree. The report says the whole-suite coverage run preceded analyzer-driven test edits and only targeted classes were rerun afterward (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-C6-report.md:87`).
- Cannot verify from diff:
  - N-3 commit-message accounting. The review package exposes only the commit subject (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/review-9725d0b..49f5f23.diff:4`), not its body or the 13 base test files. The controller should compare every base `[TestMethod]` with the commit body's one-per-line dropped-method list and confirm the ported assertions retain their base strength.

### Strengths

- `BrokerWatchChannel` is a direct pipe reader with the required frame mapping: heartbeats are skipped, batches and caught-up markers are yielded, host errors become drive faults, and all other watch-invalid frames become channel loss (`MFTLib/Broker/Client/BrokerWatchChannel.cs:22`, `:32`, `:35`, `:39`, `:43`, `:46`). The underlying channel supplies stalled-frame, EOF, I/O, cancellation and disposal behavior with the drive identity preserved (`MFTLib/Broker/Client/BrokerDriveChannel.cs:60`, `:66`, `:70`, `:89`).
- The source is correctly thin and stateless: it validates cancellation, borrows a process, opens one channel, and retains no per-drive map (`MFTLib/Broker/Client/BrokerIndexWatchSource.cs:12`, `:25`). The cancellation-without-disposal regression test rereads the same handle successfully (`MFTLib.Tests/BrokerIndexWatchSourceTests.cs:189`, `:206`).
- Heartbeat and stall composition is covered at the relevant layers: the watch skips heartbeat frames (`MFTLib.Tests/BrokerLiveWatchErrorTests.cs:127`), the client stall clock is reset by heartbeats (`MFTLib.Tests/BrokerProcessLivenessTests.cs:245`), and progressing host catch-up emits heartbeats rather than stalls (`MFTLib.Tests/JournalBrokerHostLivenessTests.Revalidation.cs:51`).
- Per-drive isolation and fault classification follow W5-1 and W5-3: channel loss records post-fault state only for T while U continues (`MFTLib.Tests/BrokerIndexWatchSourceFaultTests.cs:80`), while an Error frame asserts only the Drive fault and U's continued flow (`MFTLib.Tests/BrokerIndexWatchSourceFaultTests.cs:111`).
- Process death checks both named per-drive Channel faults and one process Ended notification (`MFTLib.Tests/BrokerDeathTests.cs:81`, `:92`, `:99`, `:103`). The rewritten harness exposes no independent host-fault event; `EndHostAsync` observes failure through `BrokerProcess.QueryVolumeAsync` (`MFTLib.Tests/TestSupport/ScriptedWatchBrokerHarness.cs:54`, `:65`).
- No changed code file exceeds the approximately 500-line guideline; the largest new support file ends at `MFTLib.Tests/TestSupport/ScriptedWatchBrokerHarness.cs:292`.

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

1. **RED evidence omits the exact per-test commands and real output.** W40-R1 explicitly rejects a placeholder command. Replace the template and abbreviated table cells with the literal command used for each of the six required tests and the actual failing output from each scratch mutation (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-C6-report.md:71`, `:75`).

2. **The issue 252 regression does not establish that the stop timed out.** An already-cancelled token does not show cancellation occurring while stop teardown is pending, and the empty optional catch lets a non-cancelling stop pass. Gate the teardown, cancel after the stop is waiting, and assert `OperationCanceledException` before starting the replacement watch (`MFTLib.Tests/BrokerIndexWatchSourceTests.cs:225`, `:229`, `:233`).

3. **A ported test uses a forbidden real-time polling sleep.** `CountUnobservedAsync` performs up to forty fixed `Task.Delay(25 ms)` waits, contrary to the plan's fake-time/signal and no-polling-sleep rule. Use a bounded signal/condition approach that does not wait on wall-clock delay (`MFTLib.Tests/Index/WatchFailureObservationTests.cs:157`, `:169`).

4. **There is no final whole-suite run after the last edits.** The reported whole-suite result predates the analyzer-driven changes. The required end-of-task verification order therefore has no final-tree suite evidence; rerun the one prescribed noninteractive whole-suite coverage command after all fixes, then run the final aislop gate (`.superpowers/sdd/2026-09-28-per-drive-watch-channels/task-C6-report.md:87`, `:88`).

#### Minor (Nice to Have)

- None.

### Assessment

Task quality: Needs fixes
Reasoning: The production per-drive channel design is focused and appears correct, but the binding RED, issue 252 regression, test timing, and final verification requirements are not yet met.
