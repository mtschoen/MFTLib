# Task FX3 report: ChannelFault_NoRecovery fails on Linux with its class siblings

Status: DONE

Worktree `C:\Users\mtsch\MFTLib-worktrees\265-FX3`, branch `task/265-FX3`, base `f1a53db`
(verified). Commit `6dbf29a` "ChannelFault_NoRecovery reads its own handler's result instead of
racing it", pushed to `gitea` as `task/265-FX3`. Test-only change; production untouched.

## Verdict: (a) the TEST is wrong

The harness subscribes its own `WatchFaulted` handler (`RecordFault`) in its constructor; the test
subscribes a second handler afterwards. `RaiseWatchFaulted` delivers to them in order. The harness
handler completes the `WaitForFaultAsync` waiter (a `RunContinuationsAsynchronously` source), so the
test's continuation is queued to the thread pool while the pump thread is still about to run the
test's own handler. The test could therefore reach `Assert.IsFalse(bool?)` while its captured
`recoveryQueuedWhenRaised` was still `null`, and MSTest's `IsFalse(bool?)` fails on `null` with the
same message as on `true`. The linux-final report's reading "it was true" was an inference the
message cannot support.

Evidence (Linux, original test at f1a53db plus one diagnostic line that prints the value and the
recorded faults in the assertion message, not committed):

```
Assert.IsFalse failed. a channel fault queues no recovery; DIAGNOSTIC value at assert: null (handler not yet run) faults: Channel:T
```

8 of 10 class runs failed, every failure with value `null` and exactly one recorded fault
(`Channel:T`). No run showed `true`, and no second fault kind was ever raised, so no recovery was
queued: the production contract (a `Channel` fault queues nothing, `RecordPumpFault` in
`FileIndex.WatchPump.cs`) holds.

Production (b) checked and ruled out:
- Recovery state is per index (`DriveRuntime.Recovery`, `_recoveryCompletions`,
  `TryGetRecoveryCompletionForTest` reads `runtime.Recovery` under `_stateLock`). The only static
  seams in `MFTLib/Index` are the restore-on-dispose overrides (`CacheDirectory`, `EnumerationWalkLimit`,
  `FileEntry.Open`, `IndexedDrive`, `JournalCheckpointCheck`, `UsnJournalSettingsQuery`), none on
  the recovery path.
- `git log 38fbca3..f1a53db --stat`: nine commits (broker control pipe, diagnostics writer,
  `MftParseControl` alignment, `ParseThreadAllowance` docs, test comments/hang guards, analyzer
  settings); none touches `FileIndex`, the recovery code, `WatchHarness`, or this test. The pass on
  38fbca3 was the race going the other way, not a regression.

## Fix

Kept the interrupted lane's hypothesis edit unchanged (it matches the evidence): the test's handler
completes its own `TaskCompletionSource<bool>` (`RunContinuationsAsynchronously`) for the `Channel`
fault on `T`, and the test asserts `IsFalse(await recoveryQueuedWhenRaised.Task.WaitAsync(HangGuard))`.
The assertion is as strong as before (the value read at raise time must be false), now with no
`null` path; the await is bounded by the hang guard; ordering is by signal, no delay.

Sibling check: `RecoveringPublishedBeforeWatchFaultedRaised` reads a handler-captured value too, but
only after `WaitForRecoveryAsync`, whose ticket starts only after `RaiseWatchFaulted` has returned
from every handler, so it is ordered. `RecoveryTicketCompletion_ContinuationNotInline` already uses
its own completion source.

## RED / GREEN (Linux, llamabox, `~/scratch/mftlib-265-w3`)

Loop script `~/scratch/fx3/loop.sh`, per run:
```
dotnet test MFTLib.Tests/MFTLib.Tests.csproj --no-build --filter "FullyQualifiedName~FileIndexWatchRecoveryTests&FullyQualifiedName!~MftResultTests&FullyQualifiedName!~MftVolumeTests&FullyQualifiedName!~NativeCoverageTests&FullyQualifiedName!~NativeParserCoverageTests&FullyQualifiedName!~UsnJournalSyntheticTests" --logger "console;verbosity=normal"
```
(the class filter plus the class exclusions `scripts/coverage-linux.sh` uses; its single-test
exclusions are outside this class).

- RED (f1a53db test + diagnostic message, `dotnet build MFTLib.Tests/MFTLib.Tests.csproj` then 10 runs):
  `DONE diag: 8 of 10 failed`; each failure `Failed ChannelFault_NoRecovery` with the message above;
  e.g. run 1 `Total tests: 26, Passed: 25, Failed: 1`.
- GREEN (the fix, rebuilt, 10 runs): `DONE fix: 0 of 10 failed`, each `Total tests: 26, Passed: 26`.

## Linux whole suite on the commit

llamabox clone checked out detached at `6dbf29a9e2ff336d92680ede51d8d9dae7e762ea` (fetched from
`task/265-FX3`), `git clean -ffxd`, `./init.sh --build` (EXIT=0), `scripts/coverage-linux.sh`:
```
=== 20 passed, 0 failed ===
Passed!  - Failed:     0, Passed:  1590, Skipped:    84, Total:  1674, Duration: 25 s - MFTLib.Tests.dll (net10.0)
native lines: 74.6% (1033 out of 1385), branches: 49.1% (505 out of 1028)
managed lines: 94.71% (8394/8862), branches: 92.96% (2258/2429)
EXIT=0
```
Logs: `~/scratch/fx3/diag.out`, `fix.out`, `init-build.log`, `coverage.log` on llamabox.

## Windows

- `.\init.ps1 -Build`: EXIT=0, solution built Release|x64.
- Class 3 times: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexWatchRecoveryTests"`:
  `Passed! - Failed: 0, Passed: 26, Skipped: 0, Total: 26` x3.
- `.\scripts\run-coverage.ps1 -NonInteractive` (log `.superpowers\run-coverage.log`): Total 1941,
  Passed 1935, Skipped 6, Failed 0; line 98.6%, branch 96.5% (2512 of 2602); EXIT=0.
- `aislop scan . -d`: `99 / 100 Healthy 0 errors 5 warnings 0 fixable`: the four baseline warnings
  (`NativeSeamIsolationFixtures.cs:73`, `:79`; `CachedBlockDeletionOutcome.cs:8`, `:10`) and the ruled
  `JournalBrokerHost.cs:46` 8-parameter warning. Nothing else.

## Files changed

`MFTLib.Tests/Index/FileIndexWatchRecoveryTests.cs` (CRLF kept).

## Primary checkout

`git -C C:\Users\mtsch\MFTLib status --short`: (empty output)

## Notes

- The llamabox scratch clone is left detached at 6dbf29a, clean; the diagnostic edit was never
  committed.
- Adjacent, not investigated: other test classes that assert on a value captured in their own
  `WatchFaulted` handler after awaiting `WatchHarness.WaitForFaultAsync` would share this race;
  the handlers found by grep in `FileIndexCatchUpLossTests`, `FileIndexCallbackReentrancyTests`,
  `FileIndexMidSessionCheckpointLossTests` and `FileIndexWatchRescanTests` were not audited.

## Follow-up sweep

Commit `e4eb39f` "Continuation-inline tests assert on the observed flag each helper returns" (on
`6dbf29a`, pushed to `gitea` `task/265-FX3`). Test-only; production untouched.

### Method

`grep "WatchFaulted +=|\.Changed +="` across all of `MFTLib.Tests` (18 files, including
`TestSupport`), then each site read to decide when its captured value is read relative to the
handler that writes it. Ordering facts used (from production, unchanged):
- `ScriptedDriveWatch.Publish`/`Queue` complete only when the pump asks for the next item, which
  is after `ApplyWatchBatch` has applied the batch, raised `Changed` inline and, if a subscriber
  threw, raised the `Subscriber` fault inline. So a `Changed` handler's value or a `Subscriber`
  fault is ordered before an awaited `Publish`.
- `CatchUpLost` is raised inline in `RunScanOperationAsync` (`FileIndex.CatchUp.cs:66`), so it is
  ordered before the `RescanAsync` that ran it completes.
- A recovery starts only after `RaiseWatchFaulted` returns from every handler, so its ticket
  completion (`WaitForRecoveryAsync`) is ordered after every handler of the fault that queued it.
- `WaitForFaultAsync` is the only unordered case: the harness handler releases it before any
  handler subscribed later has run.

### Instances of the race (test handler value read after `WaitForFaultAsync`)

| File | Test | Result |
| --- | --- | --- |
| `FileIndexWatchRecoveryTests.cs` | `ChannelFault_NoRecovery` | the only instance; fixed in `6dbf29a` |

No other test reads a value captured by its own handler after `WaitForFaultAsync`. Sites checked
and found ordered (no change): `FileIndexCatchUpLossTests.cs:66` (read after `RescanAsync`),
`FileIndexCatchUpLossTests.Operations.cs:25` (signal), `FileIndexCallbackReentrancyTests.cs` (all
handlers report through `NewSignal` tasks awaited under the hang guard),
`FileIndexMidSessionCheckpointLossTests.cs:294` (own `TaskCompletionSource`),
`FileIndexWatchRecoveryTests.cs:245` (read after `WaitForRecoveryAsync`) and `:419` (own signal),
`FileIndexWatchRescanTests.cs:556` (`NextFaultOf`, own signal), `ConsumerJournalIsolationTests.cs:165`,
`FileIndexCheckpointLossDetectionTests.cs:216`, `WatchFailureObservationTests.cs:65` (own signals),
`FileIndexWatchFaultTests.cs:139,206`, `FileIndexPerDriveWatchTests.cs:271,289`,
`FileIndexWatchPumpTests.cs:19,68,162`, `FileIndexWatchRecoveryFaultTests.cs:101` (read after an
awaited `Publish`), `FileIndexWatchTests.cs` (synchronous `ApplyJournalEntries`),
`TestSupport/ScriptedWatchBrokerHarness.cs:302`, `TestSupport/CrossDriveScenario.cs:233`,
`TestSupport/WatchHarness.cs` (the harness's own handlers).

No instance hides a production defect.

### Assertion form: nullable bool passed to `Assert.IsFalse`

`grep "bool? \w+ = null"` over `MFTLib.Tests`; three tests passed a captured `bool?` to
`Assert.IsFalse` (ordered by an awaited task, but `null` and `true` fail alike):

| File | Test | What changed |
| --- | --- | --- |
| `FileIndexBatchedOperationTests.cs` | `BatchedWait_SettledByPumpFault_ContinuationNotInline` | the local helper returns `Task<bool>` (the flag it observed); the test asserts `IsFalse(await waiter.WaitAsync(HangGuard))` |
| `FileIndexPerDriveWatchTests.Lifecycle.cs` | `PumpFaultSettlesWaiter_ContinuationNotInline` | same |
| `FileIndexPerDriveWatchTests.Lifecycle.cs` | `TokenCancelledInsideAHandler_NeverRunsTheWaitersContinuationInline` | `ObserveAsync` returns `Task<bool>`; the test asserts on `await Task.WhenAll(waiter, stopper).WaitAsync(HangGuard)` elements |

Every assertion keeps its message and strength. Left as they are: `FileIndexConcurrentRescanTests.cs:210-211`
(`Assert.AreEqual(false/true, bool?)`, whose failure message shows a null distinctly) and the two
`WatchCatchUpState?` captures (`AreEqual`, same reason; both ordered as above).

### Verification

- Windows, touched classes 3 times:
  `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~FileIndexBatchedOperationTests|FullyQualifiedName~FileIndexPerDriveWatchTests"`
  -> `Passed! - Failed: 0, Passed: 54, Skipped: 0, Total: 54` x3. Test build: 0 warnings, 0 errors.
- Linux (llamabox, detached at `e4eb39f0257c3e97c73a8d2aba68774d1cb94300`, `~/scratch/fx3/loop2.sh`):
  filter `(FullyQualifiedName~FileIndexBatchedOperationTests|FullyQualifiedName~FileIndexPerDriveWatchTests|FullyQualifiedName~FileIndexWatchRecoveryTests)`
  plus the coverage-linux class exclusions, 10 runs: every run `Total tests: 80 Passed: 80`;
  `DONE sweep: 0 of 10 failed`.
- `.\scripts\run-coverage.ps1 -NonInteractive` (`.superpowers\run-coverage-sweep.log`): Total 1941,
  Passed 1935, Skipped 6, Failed 0; line 98.6%, branch 96.5% (2513 of 2602); EXIT=0.
- `aislop scan . -d`: `99 / 100 Healthy 0 errors 5 warnings 0 fixable`: the four baseline warnings and
  the ruled `JournalBrokerHost.cs:46` warning; nothing else.
- Line endings: both touched files are LF at HEAD and stay LF.
- `git -C C:\Users\mtsch\MFTLib status --short`: (empty output)
