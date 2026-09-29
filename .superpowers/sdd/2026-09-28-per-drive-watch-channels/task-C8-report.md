# Task C8 report: cross-drive liveness scenarios

Worktree `C:\Users\mtsch\MFTLib-worktrees\265-C8`, branch `task/265-C8`, base `d1a20a999a45ad67557ea9536e759d8a1da73e98` (confirmed by `git rev-parse HEAD` first).
Commit `6edaa56` "Cross-drive liveness scenarios over the broker".

Status: DONE. No production code changed; no scenario failed on arrival, so no defect found in C5, C6, C7, B5 or B6.

## What was built

- `MFTLib.Tests/BrokerCrossDriveLivenessTests.cs` (5 tests) and `BrokerCrossDriveLivenessTests.CatchUp.cs` (3 tests), one `[TestClass] [DoNotParallelize]` partial class.
- `MFTLib.Tests/TestSupport/CrossDriveScenario.cs`: an in-process `FileIndex` over `T` and `U` on a real `BrokerProcess` served by an in-process `JournalBrokerHost` through `BrokerTestHarness`, a `FakeTimeProvider` on the host and another on the client, owned cache directory, a `WatchFaulted` recorder, per-drive scan and channel counters, and `AdvanceIntervalAsync`. That method moves the host clock one heartbeat interval, waits for the sender's visit (`HeartbeatVisitedForTest`), waits until the client has read every heartbeat the host wrote to each measured pipe (a `ReadCounter` on each client pipe end), and only then moves the client clock, so a client stall means only what the host really did not send. A pipe that carried a frame since the last visit is expected to skip the visit (the sender's rule); the silent drives are named by the test.
- `ScriptedWatchBrokerHarness` (existing) gained optional seams only: `ScriptedBrokerSeams` (host catch-up source, host clock, client options, heartbeat hook, client stream wrapper), `SetTip` per drive (a watch armed below the tip has a backlog), and `ScriptedHostWatch.WedgeNextRunInProcessing` (a watch run that publishes a processing step through the host's `IBrokerOperationReporter` and stays there). No harness member observes host failures (C2-Q1): faults reach tests only through `WatchFaulted`, `DriveStatus`, `BrokerProcess.HasEnded/Ended` and thrown exceptions.
- Tests use no real-time delay, no elapsed-time assertion, and every await is bounded (`HangGuard` or the harness token). The catch-up tests also assert both fake clocks never moved.

## Per-test table

Mutation command for every row: an uncommitted scratch edit applied by `.superpowers/mutate.py <file> <old> <new> <filter> <label>` (gitignored), which builds, runs `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter <filter>`, and reverts the file. Full outputs are in `.superpowers/mutation-<label>.log` in the worktree; `git status` after each showed only my own files.

| Test | Precondition proven | Shown failing by (scratch edit) | Real failing output |
|---|---|---|---|
| `OneDriveStallsWhileOthersFlow` | Per-channel liveness is independent: T's host write is held, T (silent 25 s on the client clock) is still healthy, faults with `Channel` and `BrokerChannelLostException` "No frame from the broker for 30 seconds" exactly at 30 s; U (a backlog delivered and caught up while T is stuck) and its host run, the control pipe and the process are untouched, U keeps heartbeating to 60 s more and applies a later batch; no recovery scan for T | M2: `BrokerFrameReader.CheckStall` claim condition `waited >= BrokerLiveness.StallLimit` changed to `waited >= TimeSpan.MaxValue`; filter `OneDriveStalls` | `Failed OneDriveStallsWhileOthersFlow [10 s]  System.TimeoutException: The operation has timed out.` at the `FaultAsync(Channel, 'T')` await (T never faults). Also M5 (heartbeat no longer restarts the client's stall window: `_readStarted` set only on the first read): `OneDriveStallsWhileOthersFlow` fails with `System.TimeoutException` (U stalls too) |
| `HostWatchdogNamesWedgedLoop_FaultsChannelWithHostMessage_NoRecovery` | Host watchdog: T's loop held in `Processing("wedged loop")` is heartbeated inside the 30 s processing limit (client never stalls), then the host writes `Stalled`; the index gets `Channel` for T carrying the host's message ("wedged loop", "made no progress for 30 seconds"), T `Faulted` (not `Recovering`), only a `Channel` fault, no recovery scan, U untouched | M3: `HostPipeWriter.DecideLocked` `case Processing when now - state.Since < BrokerLiveness.ProcessingLimit` changed to `< TimeSpan.MaxValue`; filter `HostWatchdog` | `Failed HostWatchdogNamesWedgedLoop_... [10 s]  System.TimeoutException` at the `Channel` fault await. M5 also fails it: `StringAssert.Contains failed. String 'No frame from the broker for 30 seconds' does not contain string 'wedged loop'` (the client's own stall replaced the host message) |
| `IdleWatchOverBroker_StaysAliveUnderFakeClock` | Heartbeats alone keep both idle watch pipes and the control pipe alive for 120 s (4x the stall limit) on both clocks: no fault, `HasEnded` false, both drives `CaughtUp`, a later batch applies | M1: `BrokerHeartbeatSender.Run` `pipe.Visit(now);` replaced by `_ = now;` (no heartbeats); filter `BrokerCrossDriveLivenessTests` | `Failed IdleWatchOverBroker_StaysAliveUnderFakeClock [10 s]  The client never read the control pipe's expected heartbeat: 94 of 99 bytes.` M5: `... 119 of 124 bytes.` (the control pipe's stall ended the process) |
| `IdleSessionOverBroker_NoRequestsNoChannels_KeepsProcessAlivePastStallLimit` (W4-2) | A session with no request and no channel, on a client clock moved 300 s (10x the limit), keeps `HasEnded` false, never raises `Ended`, and still answers `QueryVolumeAsync` | M1 (same edit) | `Failed IdleSessionOverBroker_NoRequestsNoChannels_... [10 s]  The client never read the control pipe's expected heartbeat: 76 of 81 bytes.` M5: `101 of 106 bytes.` |
| `HostErrorOverBroker_RecoversByRescan` (W5-3) | A host `Error` frame is `WatchFault(Drive, T)` with the host's message; the index rescans T through a scan channel of its own (2 scans total), restarts T's watch on a new pipe from the new block's cursor (`DefaultTip`), T reaches `CaughtUp` and applies a batch on it, only a `Drive` fault (no `Recovery`), T's block is `scan-T-2.txt`; U is not rescanned and its pipe is not reopened | M4: `FileIndex.WatchPump` `var recovers = fault.Kind is Drive or Apply;` changed to `var recovers = false;`; filter `HostErrorOverBroker` | `Failed HostErrorOverBroker_RecoversByRescan [10 s]  System.TimeoutException` awaiting the second host watch run. (First attempt, `StartRecovery` call removed, left the recovery ticket uncompleted and hung index disposal; that hang is why `mutate.py` now passes `RunConfiguration.TestSessionTimeout`.) |
| `CatchUpLostOverBroker_FileIndexRescansThreeTimesThenStops` | T opens exactly three scan channels, raises three `CatchUpLost` faults (counts 1,2,3; `RecoveryStopped` on the third; no `Recovery`); `RescanAsync(T)` throws the third loss; T keeps its third block (`scan-T-4.txt`), count 3, `ScanCatchUp`/`CheckpointTrimmed` report whose `SizeThatWouldHaveRetained` equals `JournalSizeArithmetic.SizeThatWouldHaveRetained(100, 900, 64)` and equals the exception's report; `StartWatchingAsync(T)` refused; U rescans normally in the same run; after the source is fixed `RescanAsync(T)` succeeds, count 0, a start is accepted from the fresh cursor; neither clock moved | M6: `FileIndex.LostCatchUpRecoveryLimit` 3 changed to 4. M7: `RunScanOperationAsync` `if (lost.RecoveryStopped)` changed to `if (lost.RecoveryStopped \|\| lost.ConsecutiveLostCatchUps >= 1)`; filter `CatchUpLostOverBroker` | M6: `Assert.AreEqual failed. Expected:<3>. Actual:<4>. T opens exactly three scan channels`. M7: `Expected:<3>. Actual:<1>. T opens exactly three scan channels` |
| `CatchUpLostOnceOverBroker_SecondScanSucceeds` | One lost catch-up retries: two channels, one fault (count 1, not stopped), count back to 0, the `ScanCatchUp` report kept, block is the second scan's (`scan-T-3.txt`), watch starts from its cursor | M7 (same edit), filter `CatchUpLostOnceOverBroker` | `MFTLib.Index.JournalCatchUpLostException: Drive T: the journal no longer held the cursor armed before the scan when the scan finished, so its catch-up was lost (1 in a row). The drive is being scanned again.` thrown out of `RescanAsync` |
| `CatchUpFailureNotProvenOverBroker_ScanFailsWithoutRetry` | A failed catch-up with the armed cursor still retained is a drive `Error`: the rescan fails (`InvalidOperationException` carrying the host's message) after exactly one scan channel, count unchanged, no report, no fault, the open's block stays | M8: `JournalCheckpointCheck.Check` `if (checkpointUsn >= firstUsn)` changed to `if (checkpointUsn == long.MaxValue)` (a retained cursor is reported lost); filter `CatchUpFailureNotProvenOverBroker` | `MFTLib.Index.JournalCatchUpLostException: Drive T: 3 scans in a row lost their journal catch-up ... Grow the drive's USN journal to at least 896 bytes` thrown where `InvalidOperationException` was expected |

Note on M1: with heartbeats removed the failure surfaces through the scenario's own synchronization ("client never read the expected heartbeat") rather than through a client stall, because the helper waits for each heartbeat before it moves the client clock. M5 is the mutation that shows the client stall itself firing under otherwise healthy heartbeats. `OneDriveStallsWhileOthersFlow` under M1 and `HostWatchdog...` under M1 fail the same way (both listed in `mutation-M1.log`).

W4-2 coverage: the held-write scenario (`OneDriveStallsWhileOthersFlow`) is the client-side "only X faults, Y keeps receiving heartbeats and watching" assertion; the idle-session test is the "`HasEnded` stays false" assertion. C5-Q1 (a progressing `Processing` pipe heartbeats) is exercised by the wedged-loop test's 5 heartbeat intervals before the stall; C5-Q2 (a pipe whose write is in flight is skipped; the client stall ends it) by `OneDriveStallsWhileOthersFlow`.

## Verification

- Targeted, iterated 12 times in a row, no flake: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerCrossDriveLivenessTests"` -> `Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8, Duration: ~200 ms` each run. With the neighbouring `BrokerFileIndexRescanTests` and `BrokerLiveWatchErrorTests`: `Passed: 18`.
- Whole suite once, after the last edit and on the committed tree: `.\scripts\run-coverage.ps1 -NonInteractive` -> `Test Run Successful. Total tests: 1841  Passed: 1835  Skipped: 6`, `Line coverage: 97.8%` (skips are the existing admin-only tests).
- aislop once, after the last edit: `aislop scan .` -> `99 / 100  Healthy  0 errors  ·  5 warnings  ·  0 fixable`. The five: the four baseline warnings (`NativeSeamIsolationFixtures.cs:73` and `:79`, `CachedBlockDeletionOutcome.cs:8` and `:10`) and the ruled 8-parameter `JournalBrokerHost` constructor. An earlier scan had two `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` warnings in `CrossDriveScenario.cs`; fixed by constructing the broker in the scenario constructor and holding the index in a nullable field.
- Line endings CRLF; no em or en dashes in the files I wrote.

## Files changed

- Created `MFTLib.Tests/BrokerCrossDriveLivenessTests.cs`, `MFTLib.Tests/BrokerCrossDriveLivenessTests.CatchUp.cs`, `MFTLib.Tests/TestSupport/CrossDriveScenario.cs`.
- Modified `MFTLib.Tests/TestSupport/ScriptedWatchBrokerHarness.cs` (seams described above; `ScriptedHostWatch.RunAsync` now takes the host's operation reporter).

## Self-review, concerns, adjacent notes

- `FileIndex.WaitForCatchUpAsync(CancellationToken)` (all drives) named in AGENTS.md architecture text does not exist on this base; only the per-drive overload does. The tests wait per drive. Doc mismatch, not fixed.
- The scenarios rely on `HostPipeWriter`'s skip-after-write rule for the exact heartbeat count; `CrossDriveScenario` derives it from bytes the client read, so a change to that rule fails the scenarios by design (with a message naming the pipe).
- The catch-up tests hold `JournalIsolation.OverrideJournalWindow` for the whole test and dispose it last (declared before the scenario), per its documented lifetime; the class is `[DoNotParallelize]`.
- The brief's "U yields batches and catches up while T is stuck" is done with a per-drive tip above U's block cursor, so U really has a backlog to read.

Primary checkout: `git -C C:\Users\mtsch\MFTLib status --short` -> (empty)

## Fix round 1

Commit `c18bf01` "Cross-drive scenario cleanup is bounded".

### Finding 2: bounded cleanup
`CrossDriveScenario.DisposeAsync` now awaits `_index.DisposeAsync()` and `Broker.DisposeAsync()` each with `.AsTask().WaitAsync(HangGuard)`, in nested try/finally so the broker and the cache directory are always released; the open failure path (`CrossDriveScenario.cs:88`) awaits `scenario.DisposeAsync().AsTask().WaitAsync(HangGuard)`. Targeted class after the change: `Passed: 8, Failed: 0`.

### Finding 1: per-scenario see-it-fail evidence (all re-run this round, real output)
Every run: the edit is applied uncommitted by `.superpowers/mutate.py` (gitignored), then `dotnet build MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64`, then the literal test command shown, then the file is restored (`git status` clean afterwards). Full transcript: `.superpowers/mutations-final.log` in the worktree. Line numbers are those of the base tree.

Not practical: none; every scenario has at least one disabled precondition below. For the no-heartbeat rows (F5, F7) failure surfaces through the scenario's own wait (`The client never read the control pipe's expected heartbeat`) rather than a client stall; F2, F4, F6, F8 are the rows where the client's stall limit itself fires.

#### OneDrive / stall never fires
- Edit: `MFTLib/Broker/Client/BrokerFrameReader.cs:172`: before `if (_reading is { Settled: false } read && waited >= BrokerLiveness.StallLimit)`, after `... && waited >= TimeSpan.MaxValue)`
- Command: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerCrossDriveLivenessTests.OneDriveStallsWhileOthersFlow" -- RunConfiguration.TestSessionTimeout=90000`
- Output:
```
  Failed OneDriveStallsWhileOthersFlow [10 s]
System.TimeoutException: The operation has timed out.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 10 s - MFTLib.Tests.dll (net10.0)
```

#### OneDrive / heartbeat does not restart stall window
- Edit: `MFTLib/Broker/Client/BrokerFrameReader.cs:106`: before `_readStarted = _timeProvider.GetTimestamp(); (every BeginRead)`, after `assigned only when _timer is null (first read), so a heartbeat does not restart the window`
- Command: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerCrossDriveLivenessTests.OneDriveStallsWhileOthersFlow" -- RunConfiguration.TestSessionTimeout=90000`
- Output:
```
  Failed OneDriveStallsWhileOthersFlow [10 s]
System.TimeoutException: The operation has timed out.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 10 s - MFTLib.Tests.dll (net10.0)
```

#### Wedged / watchdog never stalls
- Edit: `MFTLib/Broker/Host/HostPipeWriter.cs:190`: before `case ChannelOperationKind.Processing when now - state.Since < BrokerLiveness.ProcessingLimit:`, after `... < TimeSpan.MaxValue:`
- Command: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerCrossDriveLivenessTests.HostWatchdogNamesWedgedLoop_FaultsChannelWithHostMessage_NoRecovery" -- RunConfiguration.TestSessionTimeout=90000`
- Output:
```
  Failed HostWatchdogNamesWedgedLoop_FaultsChannelWithHostMessage_NoRecovery [10 s]
System.TimeoutException: The operation has timed out.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 10 s - MFTLib.Tests.dll (net10.0)
```

#### Wedged / heartbeat does not restart stall window
- Edit: `MFTLib/Broker/Client/BrokerFrameReader.cs:106`: before `same as F2`, after `same as F2`
- Command: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerCrossDriveLivenessTests.HostWatchdogNamesWedgedLoop_FaultsChannelWithHostMessage_NoRecovery" -- RunConfiguration.TestSessionTimeout=90000`
- Output:
```
  Failed HostWatchdogNamesWedgedLoop_FaultsChannelWithHostMessage_NoRecovery [10 s]
System.TimeoutException: The operation has timed out.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 10 s - MFTLib.Tests.dll (net10.0)
```

#### IdleWatch / no heartbeats
- Edit: `MFTLib/Broker/Host/BrokerHeartbeatSender.cs:73`: before `pipe.Visit(now);`, after `_ = now;`
- Command: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerCrossDriveLivenessTests.IdleWatchOverBroker_StaysAliveUnderFakeClock" -- RunConfiguration.TestSessionTimeout=90000`
- Output:
```
  Failed IdleWatchOverBroker_StaysAliveUnderFakeClock [10 s]
   The client never read the control pipe's expected heartbeat: 94 of 99 bytes.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 10 s - MFTLib.Tests.dll (net10.0)
```

#### IdleWatch / heartbeat does not restart stall window
- Edit: `MFTLib/Broker/Client/BrokerFrameReader.cs:106`: before `same as F2`, after `same as F2`
- Command: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerCrossDriveLivenessTests.IdleWatchOverBroker_StaysAliveUnderFakeClock" -- RunConfiguration.TestSessionTimeout=90000`
- Output:
```
  Failed IdleWatchOverBroker_StaysAliveUnderFakeClock [10 s]
   The client never read the control pipe's expected heartbeat: 119 of 124 bytes.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 10 s - MFTLib.Tests.dll (net10.0)
```

#### IdleSession / no heartbeats
- Edit: `MFTLib/Broker/Host/BrokerHeartbeatSender.cs:73`: before `pipe.Visit(now);`, after `_ = now;`
- Command: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerCrossDriveLivenessTests.IdleSessionOverBroker_NoRequestsNoChannels_KeepsProcessAlivePastStallLimit" -- RunConfiguration.TestSessionTimeout=90000`
- Output:
```
  Failed IdleSessionOverBroker_NoRequestsNoChannels_KeepsProcessAlivePastStallLimit [10 s]
   The client never read the control pipe's expected heartbeat: 76 of 81 bytes.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 10 s - MFTLib.Tests.dll (net10.0)
```

#### IdleSession / heartbeat does not restart stall window
- Edit: `MFTLib/Broker/Client/BrokerFrameReader.cs:106`: before `same as F2`, after `same as F2`
- Command: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerCrossDriveLivenessTests.IdleSessionOverBroker_NoRequestsNoChannels_KeepsProcessAlivePastStallLimit" -- RunConfiguration.TestSessionTimeout=90000`
- Output:
```
  Failed IdleSessionOverBroker_NoRequestsNoChannels_KeepsProcessAlivePastStallLimit [10 s]
   The client never read the control pipe's expected heartbeat: 101 of 106 bytes.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 10 s - MFTLib.Tests.dll (net10.0)
```

#### HostError / no recovery
- Edit: `MFTLib/Index/FileIndex.WatchPump.cs:217`: before `var recovers = fault.Kind is WatchFaultKind.Drive or WatchFaultKind.Apply;`, after `var recovers = false;`
- Command: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerCrossDriveLivenessTests.HostErrorOverBroker_RecoversByRescan" -- RunConfiguration.TestSessionTimeout=90000`
- Output:
```
  Failed HostErrorOverBroker_RecoversByRescan [10 s]
System.TimeoutException: The operation has timed out.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 10 s - MFTLib.Tests.dll (net10.0)
```

#### ThreeTimes / limit 4
- Edit: `MFTLib/Index/FileIndex.CatchUp.cs:9`: before `LostCatchUpRecoveryLimit = 3;`, after `LostCatchUpRecoveryLimit = 4;`
- Command: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerCrossDriveLivenessTests.CatchUpLostOverBroker_FileIndexRescansThreeTimesThenStops" -- RunConfiguration.TestSessionTimeout=90000`
- Output:
```
  Failed CatchUpLostOverBroker_FileIndexRescansThreeTimesThenStops [118 ms]
   Assert.AreEqual failed. Expected:<3>. Actual:<4>. T opens exactly three scan channels
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 128 ms - MFTLib.Tests.dll (net10.0)
```

#### ThreeTimes / no retry
- Edit: `MFTLib/Index/FileIndex.CatchUp.cs:67`: before `if (lost.RecoveryStopped)`, after `if (lost.RecoveryStopped || lost.ConsecutiveLostCatchUps >= 1)`
- Command: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerCrossDriveLivenessTests.CatchUpLostOverBroker_FileIndexRescansThreeTimesThenStops" -- RunConfiguration.TestSessionTimeout=90000`
- Output:
```
  Failed CatchUpLostOverBroker_FileIndexRescansThreeTimesThenStops [114 ms]
   Assert.AreEqual failed. Expected:<3>. Actual:<1>. T opens exactly three scan channels
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 123 ms - MFTLib.Tests.dll (net10.0)
```

#### Once / no retry
- Edit: `MFTLib/Index/FileIndex.CatchUp.cs:67`: before `same as F11`, after `same as F11`
- Command: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerCrossDriveLivenessTests.CatchUpLostOnceOverBroker_SecondScanSucceeds" -- RunConfiguration.TestSessionTimeout=90000`
- Output:
```
  Failed CatchUpLostOnceOverBroker_SecondScanSucceeds [109 ms]
MFTLib.Index.JournalCatchUpLostException: Drive T: the journal no longer held the cursor armed before the scan when the scan finished, so its catch-up was lost (1 in a row). The drive is being scanned again.
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 118 ms - MFTLib.Tests.dll (net10.0)
```

#### NotProven / retained cursor reported lost
- Edit: `MFTLib/Index/JournalCheckpointCheck.cs:79`: before `if (checkpointUsn >= firstUsn)`, after `if (checkpointUsn == long.MaxValue)`
- Command: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerCrossDriveLivenessTests.CatchUpFailureNotProvenOverBroker_ScanFailsWithoutRetry" -- RunConfiguration.TestSessionTimeout=90000`
- Output:
```
  Failed CatchUpFailureNotProvenOverBroker_ScanFailsWithoutRetry [112 ms]
MFTLib.Index.JournalCatchUpLostException: Drive T: 3 scans in a row lost their journal catch-up, so the drive keeps its last block, which cannot be watched. Grow the drive's USN journal to at least 896 bytes (BrokerProcess.GrowUsn
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 121 ms - MFTLib.Tests.dll (net10.0)
```

### Verification after the last edit
- Targeted: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerCrossDriveLivenessTests"` -> `Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8`.
- Whole suite once: `.\scripts\run-coverage.ps1 -NonInteractive` -> `Test Run Successful. Total tests: 1841  Passed: 1835  Skipped: 6` (Failed 0), line coverage 97.8%.
- aislop once: `aislop scan .` -> `99 / 100  Healthy  0 errors  ·  5 warnings  ·  0 fixable`: the four baseline warnings plus the ruled 8-parameter `JournalBrokerHost` warning.
- Primary checkout: `git -C C:\Users\mtsch\MFTLib status --short` -> (empty)

## Fix round 2

Commit `1946ef7` "Cross-drive scenario cleanup runs every step after an open failure". `CrossDriveScenario.cs:88` now awaits `scenario.DisposeAsync()` directly; each step inside (index, broker) is already bounded by `WaitAsync(HangGuard)` and the cache directory deletion always runs, so no outer guard can abandon a later step.

- Targeted: `dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerCrossDriveLivenessTests"` -> `Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8`.
- aislop: `aislop scan .` -> `99 / 100  Healthy  0 errors  ·  5 warnings  ·  0 fixable` (four baseline plus the ruled JournalBrokerHost warning).
