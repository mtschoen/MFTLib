# Task FX2 report: DisposeDuringRecovery_CancelsIt flake

Status: DONE_WITH_CONCERNS (concern: the flake did not reproduce locally, 0 of 200 before the fix;
the fix removes the one interleaving the analysis found that can fail the test).

Worktree `C:\Users\mtsch\MFTLib-worktrees\265-FX2`, branch `task/265-FX2`, base `2a8b517` (verified).
Commit `a45d467` "DisposeDuringRecovery_CancelsIt asserts on the recovery's own completion". Test-only.

## Reproduction

Loop script `.superpowers/loop.sh` in the worktree (one `dotnet test` per run, bounded count),
four loops in parallel for load:

```
for id in 1 2 3 4; do bash .superpowers/loop.sh $id 50 "FullyQualifiedName~DisposeDuringRecovery_CancelsIt" > .superpowers/loop-$id.out 2>&1 & done; wait
```
where each run is
`dotnet test C:/Users/mtsch/MFTLib-worktrees/265-FX2/MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~DisposeDuringRecovery_CancelsIt"`.

Before the fix: `loop 1: 0 of 50 failed`, `loop 2: 0 of 50`, `loop 3: 0 of 50`, `loop 4: 0 of 50`
(0 of 200). After the fix (loops 5 to 8, same command): 0 of 200.

## Analysis of every interleaving

The test holds the recovery's production on a gate, then asserts `recovery` is not complete,
disposes the index, and asserts `recovery.IsCompleted`, then that the faults are exactly `[Drive]`.

1. **The asserted task is not the one disposal waits for (the cause).** `recovery` came from
   `WatchHarness.WaitForRecoveryAsync`, which returns `ticketCompletion.WaitAsync(HangGuard)`. That
   is a derived task, completed by a continuation of the ticket's completion. The ticket's
   `TaskCompletionSource` uses `RunContinuationsAsynchronously`, so that continuation is queued to
   the thread pool when `EndRecovery` completes the ticket. Disposal
   (`StopEveryWatchForDisposalAsync`) awaits the ticket's completion itself, from
   `_recoveryCompletions`, and its own resumption is also queued. Nothing orders the wrapper's
   queued continuation before `DisposeAsync` returns and the test resumes, so under thread-pool
   load (a full suite run, as in the CB lane) `recovery.IsCompleted` can read false although the
   recovery is finished. This matches a one-off failure in a full run that does not recur alone.
2. **Could disposal return before the ticket completes?** No. The ticket's completion is added to
   `_recoveryCompletions` under `_stateLock` in `QueueRecovery`, before the fault is raised and
   before the recovery runs, so it is present when disposal collects the set under the same lock
   (the held producer proves the recovery is running). It is removed only after
   `ticket.Complete()` (`EndRecovery`), so disposal's `WhenAll` includes it. Production matches the
   spec (disposal cancels queued recoveries and awaits them).
3. **Could the recovery hang or not be cancelled?** The held producer waits with the rescan's
   token, which is the ticket's cancellation source, linked to disposal, so disposal's cancel ends
   it. The test's `WaitAsync(HangGuard)` on disposal would turn a hang into a timeout, not an
   `IsCompleted` failure.
4. **Could an extra fault appear (`[Drive, Recovery]`)?** The cancelled producer's failure reaches
   `RecoverWithGateHeldAsync`, whose filter `ticket.Cancellation.IsCancellationRequested ||
   _disposed` returns no failure, so no `Recovery` fault is raised. The ticket is also cleared by
   disposal before `EndRecovery`, which reports only for a ticket still the drive's. No
   `CatchUpLost` (no loss scripted).
5. **`Assert.IsFalse(recovery.IsCompleted)` before disposal** cannot fail: the recovery is parked
   in the producer (`held.Entered` observed), so neither the ticket nor its wrapper can complete.

Only (1) can fail the test. The failing assertion was not captured from the CB lane, so this
names the only interleaving that the analysis finds possible, not a confirmed trace.

## Fix

Test side (the production contract is right). `WatchHarness.RecoveryCompletion(char)` returns the
latest recovery's unwrapped completion, the same task disposal awaits; the test asserts
`IsFalse` before disposal and `IsTrue` after it on that task. The assertion is unchanged in
strength (still "disposal cancels the recovery and waits for it"), with no sleep or retry.
No RED run: the failure did not reproduce, and a test-only fix needs no production RED.

## Verification (after the last edit)

- `dotnet test ... --filter "FullyQualifiedName~Index"`: `Passed! - Failed: 0, Passed: 932, Skipped: 6, Total: 938`.
- Loops after the fix: 0 of 200 (4 parallel loops of 50).
- `.\scripts\run-coverage.ps1 -NonInteractive`: `Total tests: 1885`, `Passed: 1879`, `Skipped: 6`,
  Failed 0; line 97.8%, branch 95.6%.
- `aislop scan . -d`: `99 / 100 Healthy 0 errors 5 warnings`: the four baseline warnings and the
  ruled `JournalBrokerHost` warning.

## Adjacent

Other tests await `WaitForRecoveryAsync` rather than reading `IsCompleted` on it, which is safe.
No other `IsCompleted` read on a derived task was found in the recovery tests.

Files: `MFTLib.Tests/TestSupport/WatchHarness.cs`, `MFTLib.Tests/Index/FileIndexWatchRecoveryTests.cs`.
