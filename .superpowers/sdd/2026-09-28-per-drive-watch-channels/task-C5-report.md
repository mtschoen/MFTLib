# Task C5 report: host liveness (operation state, heartbeat thread, watchdog, bounded catch-up)

Worktree `C:\Users\mtsch\MFTLib-worktrees\265-C5`, branch `task/265-C5`, base
`566718bb6f80795a6f58e50ee3441dc81220cdaf` (confirmed as the first action).

## What was implemented

Production (all under `MFTLib/`):

- `Broker/Host/ChannelOperationState.cs` (new): `ChannelOperationKind { Idle, WaitingOnVolume, Queued, Processing }`
  and `readonly record struct ChannelOperationState(ChannelOperationKind Kind, string Step, DateTimeOffset Since)`,
  verbatim from the brief. `Step` is `string.Empty` outside `Processing`.
- `Broker/Host/BrokerOperationState.cs` (deleted): C1's reporter (`BrokerOperationPhase`, `BrokerOperationState`)
  is replaced in place by the brief's names; nothing else read it.
- `Broker/Host/HostPipeWriter.cs` (new): one per pipe (control and every drive pipe). Holds the write lock
  (`SemaphoreSlim`), the "wrote since the last visit" flag, the operation state, the stall flag and a closed flag.
  It implements `IBrokerOperationReporter` (plus `Queued()`), so it is the reporter every source on that pipe
  receives. `WriteFrameAsync` (the old static `WriteFrameAsync` moved here, same `ClientDisconnectedException`
  mapping) restarts the progress clock and sets the flag; a pipe that reported a stall throws
  `OperationCanceledException` on any later write, so `Stalled` is always the last frame. `Visit(now)` is the
  sender's per-pipe step. The constructor takes the pipe's owner `CancellationTokenSource` (the channel's, or the
  session's for the control pipe): its token bounds sender writes, and it is cancelled after `Stalled` or a failed
  heartbeat write, until `Close()`.
- `Broker/Host/BrokerHeartbeatSender.cs` (new): one dedicated `Thread` (`IsBackground = true`) per session, woken by
  `timeProvider.CreateTimer(..., HeartbeatInterval, HeartbeatInterval)` setting a `ManualResetEventSlim`. Each wake
  visits a snapshot of the registered pipes and then invokes the test hook on the sender thread. `Dispose` stops the
  timer and joins the thread.
- Visit rules (`HostPipeWriter.Visit`): a pipe that wrote since the last visit is skipped (flag cleared). Otherwise
  control `Idle`, `WaitingOnVolume` and `Queued` start a `Heartbeat`; `Processing` with `now - Since >= ProcessingLimit`
  starts `Stalled` naming the step (`"Broker step '<step>' on pipe <tag> made no progress for 30 seconds."`); a drive
  pipe in `Idle` (before its first request) writes nothing, so the first-request timeout keeps its `Error`. A sender
  write starts only if `_writeLock.Wait(0)` succeeds (S2): a pipe with a write in flight is skipped. The sender never
  awaits a write; the write completes on its own, releases the lock, and on failure (any exception, logged to the
  diagnostics log) or after `Stalled` cancels the owner.
- `JournalBrokerHost.Session.cs`: `ControlSession` owns the control `HostPipeWriter` and the sender; the control pipe
  heartbeats whenever it wrote nothing since the last visit (S1). Disposal: sender first, then `Close()` the control
  pipe, then the session's CTS.
- `JournalBrokerHost.Channel.cs`: each drive pipe gets a `HostPipeWriter` over the channel's CTS, is registered with the
  sender from the first-request wait until the operation returns, then unregistered and closed before the CTS and the
  stream are disposed.
- `JournalBrokerHost.Frames.cs`: `DriveChannel` is now `(Stream, Drive, HostPipeWriter Pipe)`; no own lock.
- `JournalBrokerHost.cs`: the watch loop enumerates by hand, publishing `WaitingOnVolume` before each `MoveNextAsync`
  and `Processing("journal batch")` per batch; `ArmWatchAsync` publishes `WaitingOnVolume` around the tip query. Two
  internal test hooks, set before serving: `OperationStatePublishedForTest` (tag, state per publication) and
  `HeartbeatVisitedForTest` (runs on the sender thread after each visit). The public `CatchUp(string, cursor)` method
  is deleted (its only caller was the scan, which now runs the bounded loop; see "Deleted").
- `JournalBrokerHost.Scan.cs`: `Queued` while admission waits (existing), `WaitingOnVolume` around the cursor's volume
  open, `Processing("block write")` per record batch (host-side wrapper over the source), the progress pump's throttle
  on `TimeProvider` (`GetTimestamp`/`GetElapsedTime`), and the bounded catch-up loop: `Processing("journal catch-up")`
  on entry, then calls of at most `BrokerLiveness.CatchUpBufferReadsPerCall`, republishing after each call that
  advanced the cursor, until a call returns its cursor unchanged. A call that throws ends catch-up without a retry; the
  existing C1 journal check chooses `CatchUpLost` or `Error` (L1). The token is checked between calls.
- `JournalBrokerHost.Sources.cs`: `ReadJournal(drive, since, maximumBufferReads)` uses A4's `ReadUsnJournalBounded`.
- `Broker/Sources/UsnJournalCatchUpSource.cs`: gains `int maximumBufferReads` (W2-1); doc states that an unchanged cursor
  means the tip.
- `Broker/BrokerLiveness.cs`: adds `public const int CatchUpBufferReadsPerCall = 256;` (W4-1: nothing redefined).
- `Broker/SharedMemory/IBlockSectionWriter.cs`: `Write(sectionName, cursor, batches, filter, BlockWriteReporting reporting,
  cancellationToken)`; new `public readonly record struct BlockWriteReporting(IProgress<BlockWriteProgress>? Progress,
  IBrokerOperationReporter? Operation)`. The bundle replaces the old `progress` parameter because adding the reporter
  as a seventh parameter fails the aislop too-many-parameters rule.
- `Broker/SharedMemory/RealBlockSectionWriter.cs`: passes `_ => operation.Processing("block flush")` to W40's
  `BlockWriter.Complete(DateTime, Action<long>?)`, so each flushed range republishes the scan pipe's state (W4-3/W40:
  no `Libc.cs`, no `BlockFile*`/`BlockWriter`/`Kernel32` edits, no caller sweep).
- `Benchmark/BenchmarkRunner.cs`: passes `default` reporting.

Tests:

- `MFTLib.Tests/JournalBrokerHostLivenessTests.cs` and `.Progress.cs` (new, `[DoNotParallelize]` because the L1 case
  installs `JournalCheckpointCheck`'s process-wide override): the 11 tests below. A `Liveness` helper builds the host on
  a `FakeTimeProvider` with both hooks; `AdvanceOneIntervalAsync` advances 5 s and waits (bounded) for that visit, so
  ticks never merge. No real-time waits, no elapsed-time assertions; every await is bounded by `HangGuard`.
- `TestSupport/HostChannelHarness.cs`: reads skip `Heartbeat` frames unless `includeHeartbeats: true` (as the client
  does), so existing exact-frame tests stay deterministic now that a fake-clock advance can produce heartbeats; new
  optional `wrapDrivePipe` constructor parameter wraps the default connector's host end.
- `TestSupport/BrokerTestStreams.cs`: `HeldWrites` (starts every write and never finishes it; counts attempts).
- `TestSupport/CatchUpSources.cs` (new): `ToTip(tip, entries)` fake honoring the bounded-read contract.
- Existing fakes updated for the new signatures; fakes that returned an advanced cursor on every call (which would now
  loop forever) were made tip-aware: `BrokerProcessTests.Scan.cs`, `JournalBrokerHostTests.Scan.cs`,
  `JournalBrokerHostChannelTests.Scan.cs`, `JournalBrokerHostTests.WatchDiagnosticsFilter.cs`,
  `JournalBrokerHostBlockScanTests.cs` (now also asserts the bound 256 is passed), `JournalBrokerHostRealSeamsTests.cs`
  (native mock stops at its tip after the first read).
- `BrokerProcessTests.Disposal.cs`: `TimerCreated(ControlClosedGracePeriod, occurrence: 2)`, because the heartbeat
  timer (also 5 s) is now the first 5 s host timer; the assertion is unchanged.
- `JournalBrokerHostSourcesTests.OperationState_RecordsEachPublicationAndWhenItHappened`: C1's test ported onto
  `HostPipeWriter` (same method name, same meaning: each publication records kind, step and time on the host clock).

### Deleted

- `JournalBrokerHost.CatchUp(string, UsnJournalCursor)` (public) and its test
  `JournalBrokerHostTests.CatchUp_DelegatesToReadJournal_ReturnsAdvancedCursor`: the delegate now takes a read bound and
  the scan runs a loop over it; the single-call delegation the test pinned no longer exists. Its substance (the source
  is called with the armed cursor and its result reaches the terminal batch) is covered by
  `ScanChannel_EmitsCursorProgressReadyAndCatchUpInOrderThenCloses`, `ArmAndScan_EmitsCursorScanReadyAndCatchUp` and
  `CatchUp_BoundedReads_RepublishesPerCall`.

## Rulings applied

W2-1 (parameter plus loop), W4-1 (constants reused, one added), W4-2 (host-side assertions only, through
`HostChannelHarness`; no client assertions), W4-3/W40 (reporter only; `BlockFile_Flush_ReportsEachRange` not rewritten;
new flush-clock test instead), N-1 (test hook, `IsThreadPoolThread == false`, `IsBackground == true`, no pool
saturation), C1 deviation (`Processing(string stepName)`), C2-Q1 (no harness member observes host failures; the hooks
observe state and visits only), W40-R1 (RED evidence below).

## TDD evidence

Implemented first, then each new test was shown RED against an uncommitted scratch mutation of the production
behavior it pins (script `.superpowers/mutations.py`, log `.superpowers/mutations.log`, both git-ignored; every
mutation was reverted and a clean rebuild confirmed). Each command was
`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "<filter>"`, with the
filter naming `FullyQualifiedName~JournalBrokerHostLivenessTests.<Test>` for each test listed.

| Mutation | Tests (filter) | Failing output | Why expected |
|---|---|---|---|
| M1: `Visit` writes no heartbeat | IdleWatch_WaitingOnVolume_HeartbeatsAndNeverStalls, QueuedScan_WaitingForAdmission_Heartbeats, IdleSession_NoRequestsNoWatches_StaysAlivePastStallLimit, BlockedWriteOnX_DoesNotDelayHeartbeatsOnY_OnlyXFaults, HeartbeatSkipped_WhilePreviousWriteInFlight | first four: `System.OperationCanceledException` after 10 s (the harness read's hang guard: no heartbeat frame ever arrives); last: `Assert.AreEqual failed. Expected:<1>. Actual:<0>. The first visit starts a heartbeat, which is held.` `Failed: 5` | no heartbeat is written |
| M2: `Processing` past the limit writes no `Stalled` | WedgedProcessing_WritesStalledNamingStepAndCloses | `System.OperationCanceledException` after 10 s (no `Stalled`, no EOF). `Failed: 1` | the watchdog never fires |
| M3: a republish of the same step keeps the old `Since` | ProcessingWithProgress_NeverStalls | `Assert.IsTrue failed.` (the scan was stalled at 30 s and cancelled, so the test's step handshake failed). `Failed: 1` | progress no longer restarts the clock |
| M4: heartbeat started even when a write is in flight | BlockedWriteOnX_DoesNotDelayHeartbeatsOnY_OnlyXFaults, HeartbeatSkipped_WhilePreviousWriteInFlight | `Expected:<1>. Actual:<10>. X's first heartbeat is held, and no later heartbeat is started on X.`; `Expected:<1>. Actual:<3>. Three visits to a pipe with a held write start exactly one heartbeat.` `Failed: 2` | S2 skip removed |
| M5: sender loop queued to the thread pool | HeartbeatSender_RunsOnDedicatedThread | `Assert.IsFalse failed. The sender must not run on the thread pool.` `Failed: 1` | N-1 |
| M6: catch-up republishes before every call | CatchUp_BoundedReads_RepublishesPerCall, CatchUp_SecondBoundedReadFails_WritesCatchUpLostAndNoBatch | `Expected:<3>. Actual:<7>. The progress clock restarts once per call that returned a chunk.`; `Expected:<1>. Actual:<3>. Only the call that returned a chunk restarted the progress clock.` `Failed: 2` | per-call republish rule |
| M6b: a failed catch-up call is retried once | CatchUp_SecondBoundedReadFails_WritesCatchUpLostAndNoBatch | `Expected:<2>. Actual:<3>. A failed bounded read is not retried.` `Failed: 1` | L1 no-retry rule |
| M7: `RealBlockSectionWriter` passes no range reporter | BlockFlush_EachRange_RestartsScanPipeProgressClock | `Expected:<3>. Actual:<0>. Each flushed range republishes the scan pipe's state.` `Failed: 1` | W40 reporter wiring |

GREEN:
`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostLivenessTests"`
-> `Passed!  - Failed: 0, Passed: 11, Skipped: 0, Total: 11, Duration: 115 ms` (run five more times in a row, all 11/11).

How "restarted N times" is counted: the host publishes `Processing("journal catch-up")` once on entering catch-up (a
state change) and once after each call that advanced the cursor. The test counts republishes, that is publications of
the step that follow a publication of the same step on the same pipe: 3 for three chunks, 1 for one chunk then a
failure, matching the brief.

Targeted host suites:
`dotnet test ... --no-build --filter "FullyQualifiedName~JournalBrokerHost|FullyQualifiedName~BrokerProcess|FullyQualifiedName~BrokerProtocol|FullyQualifiedName~GrowUsnJournalHost|FullyQualifiedName~VolumeQueryHost|FullyQualifiedName~BrokerFrameLength|FullyQualifiedName~NativeSeamIsolation"`
-> `Passed!  - Failed: 0, Passed: 291, Skipped: 0, Total: 291`.

## Whole suite

`.\scriptsun-coverage.ps1 -NonInteractive` (run twice: once after implementation, once after the aislop fixes;
both identical): `Total tests: 1664  Passed: 1658  Skipped: 6` (the six are Linux/Unix-only cases), 0 failed,
exit 0. Line coverage 97.3% overall, MFTLib 97.6%; `BrokerHeartbeatSender` 100%, `ChannelOperationState` 100%,
`HostPipeWriter` 95.4%, `JournalBrokerHost` 96.6%, `RealBlockSectionWriter` 100%.

## aislop

`aislop scan . -d` (0.16.0): `99 / 100  Healthy  0 errors · 5 warnings`. Remaining findings, all allowed by the gate:
`MFTLib.Tests/NativeSeamIsolationFixtures.cs:73` and `:79` (AsyncFixer01), `MFTLib/Index/CachedBlockDeletionOutcome.cs:8`
and `:10` (redundant doc comment), and the ruled parameter-count warning on the 8-parameter `JournalBrokerHost`
constructor (`MFTLib/Broker/Host/JournalBrokerHost.cs:46`). One new `aislop-ignore-next-line csharp-sync-over-async`
directive, on `_wake.Wait()` in `BrokerHeartbeatSender.Run`: a false positive (a `ManualResetEventSlim`, not a Task;
blocking the dedicated thread between visits is the design), with the reason on the directive. The scan reports 4
suppressed findings (3 before this task plus this one).

The first scan found 16 more, all fixed: formatting, AsyncFixer01 in the test helper, two RCS1139 (constructor
summaries), two AccessToDisposedClosure (semaphores replaced by `TestGate`s), an inconsistently synchronized field
(published list now read under its lock), a modified captured variable (harness `wrapDrivePipe` parameter), the
null-forgiving cast in the timer callback, four redundant XML summaries on step-name constants, and two
too-many-parameters findings (`HostPipeWriter` constructor now takes the owner CTS instead of an abandon delegate plus
token; `IBlockSectionWriter.Write` takes `BlockWriteReporting`).

## Threads and locks

- **Sender thread.** One `Thread` per `ServeAsync` session (in production, one per broker process), `IsBackground =
  true`, named "MFTLib broker heartbeat", created in `ControlSession`'s constructor and joined in its `Dispose` after the
  drain. It waits only on its own `ManualResetEventSlim`, which the `TimeProvider` timer sets every `HeartbeatInterval`;
  it resets the event before visiting, so a tick during a visit wakes the next one. A visit takes the sender's registry
  lock only to snapshot the pipe list.
- **Per-pipe locks.** Each `HostPipeWriter` has a `SemaphoreSlim` write lock held for the whole of one frame write
  (operation frames, progress pump frames and sender frames alike, so frames never interleave on that pipe only), and a
  `Lock _gate` guarding the state, the wrote-since-visit flag, the stall flag and the closed flag. `_gate` is never held
  across a write, an await, or a call into the owner other than `CancelAsync` (which runs callbacks asynchronously).
  The control pipe has its own writer; no lock is shared between pipes.
- **Why the sender can never block on a pipe write.** It takes a pipe's write lock only with `Wait(0)`; if any write
  holds it, the pipe is skipped (S2). When it gets the lock it only starts the write: `CompleteSenderWriteAsync` runs
  synchronously up to the stream's first incomplete await and returns, and every production pipe is opened with
  `PipeOptions.Asynchronous`, so a write blocked on a full pipe buffer is a pending task, not a blocked thread. The
  lock is released by that write's own completion. A pipe whose write is blocked therefore gets no further heartbeats
  (its client stall limit ends it) and delays no other pipe.
- **Teardown ordering.** A channel unregisters its writer and calls `Close()` before disposing its CTS and stream; after
  `Close()` no sender write starts and a failing one cancels nothing, so a late heartbeat failure never touches a
  disposed CTS. The session stops the sender before closing the control writer and disposing its CTS. The write
  semaphore is intentionally never disposed (`CA1001` suppressed with the reason): a write in flight may release it
  after the owner has ended.

## Files changed

New: `MFTLib/Broker/Host/ChannelOperationState.cs`, `MFTLib/Broker/Host/HostPipeWriter.cs`,
`MFTLib/Broker/Host/BrokerHeartbeatSender.cs`, `MFTLib.Tests/JournalBrokerHostLivenessTests.cs`,
`MFTLib.Tests/JournalBrokerHostLivenessTests.Progress.cs`, `MFTLib.Tests/TestSupport/CatchUpSources.cs`.
Deleted: `MFTLib/Broker/Host/BrokerOperationState.cs`.
Modified: `MFTLib/Broker/BrokerLiveness.cs`, `MFTLib/Broker/Host/JournalBrokerHost.cs`, `.Channel.cs`, `.Frames.cs`,
`.Scan.cs`, `.Session.cs`, `.Sources.cs`, `MFTLib/Broker/SharedMemory/IBlockSectionWriter.cs`,
`MFTLib/Broker/SharedMemory/RealBlockSectionWriter.cs`, `MFTLib/Broker/Sources/UsnJournalCatchUpSource.cs`,
`Benchmark/BenchmarkRunner.cs`, and tests `BrokerFrameLengthTests.cs`, `BrokerProcessLaunchTests.cs`,
`BrokerProcessTests.cs`, `BrokerProcessTests.Disposal.cs`, `BrokerProcessTests.Scan.cs`, `BrokerProtocolTests.Scan.cs`,
`GrowUsnJournalHostTests.cs`, `JournalBrokerHostBlockScanTests.cs`, `JournalBrokerHostChannelTests.cs`,
`JournalBrokerHostChannelTests.Scan.cs`, `JournalBrokerHostRealSeamsTests.cs`, `JournalBrokerHostSourcesTests.cs`,
`JournalBrokerHostTests.cs`, `JournalBrokerHostTests.Scan.cs`, `JournalBrokerHostTests.WatchDiagnosticsFilter.cs`,
`JournalBrokerHostTests.WatchSupport.cs`, `VolumeQueryHostTests.cs`, `TestSupport/BrokerTestStreams.cs`,
`TestSupport/HostChannelHarness.cs`, `TestSupport/RecordingBlockSectionWriter.cs`. All CRLF.

## Self-review, concerns, adjacent

- **Concern (design gap, not fixed): a `Processing` step that republishes but writes no frame for 30 s is killed by the
  client stall limit.** Per the brief and spec 2.2, only `Idle` (control), `WaitingOnVolume` and `Queued` heartbeat;
  `Processing` within the limit writes nothing. Most scan phases write `ScanProgress` frames, but two do not: the
  bounded catch-up after `ScanReady` (republishes per 16 MB read, no frame) and the block flush (republishes per range,
  no frame). A catch-up over a large backlog that takes more than 30 s with steady progress therefore never stalls on
  the host but is cut off by the client's 30 s stall limit (C7). A likely fix is for `Processing` with recent progress
  (within `ProcessingLimit`) to heartbeat too; that changes the spec's heartbeat table, so I did not make it.
- **Concern (spec vs brief):** spec 2.2 says a heartbeat write to X that exceeds its bound closes X; the brief's S2 says a
  pipe with a write in flight is simply skipped and the client stall limit ends it. I followed the brief. A heartbeat
  write that fails (throws) does cancel the channel, as the spec says.
- The idle-watch test arms the watch behind the tip (no leading `CaughtUp`), so the pipe writes nothing before the first
  visit and the count is exactly 24; a watch armed at the tip writes `CaughtUp` and its first visit is skipped as "wrote
  since the last visit".
- `HostPipeWriter` line coverage is 95.4%: the uncovered paths are a failed heartbeat write cancelling the owner and a
  late sender write after `Close()`. No brief test pins them; a regression test for "failed heartbeat write cancels the
  channel" would be a cheap follow-up.
- The block-flush test maps a named section just over 128 MB (DeleteOnClose temp file, untouched pages, about 60 ms);
  it is Windows-only like the existing `RealBlockSectionWriter` test.
- Heartbeat frames are skipped by `HostChannelHarness` reads by default; a test that must see them passes
  `includeHeartbeats: true`.
- Adjacent, not fixed: none found beyond the concerns above. The known frame-length overflow defect in `broker-common.md`
  is untouched.

## Primary checkout

`git -C C:\Users\mtsch\MFTLib status --short`: (empty output)

## Commit

`f25e999` Host heartbeats idle pipes and reports a wedged operation as Stalled (on `task/265-C5`).

## Fix round 1

Commit `299b75d` "Host revalidates a pipe's progress before writing Stalled and heartbeats a progressing operation"
on `task/265-C5` (on top of `f25e999`).

### Changes

1. **False Stalled race (review Important 1).** `HostPipeWriter.Visit` now takes the pipe's write lock first
   (`Wait(0)`; a write in flight skips the pipe and counts as this interval's write), then decides under `_gate` in
   `DecideLocked` from the current `_state`, its `Since` and the wrote-since-visit flag, and sets `_stalled` in the same
   critical section. While the visit holds the write lock no frame can complete, and while it holds `_gate` no
   publication can land, so a Stalled is only ever written from the pipe's state at the moment it is decided.
   `TryStartSenderWrite` is folded into `Visit`. New internal hook `HostPipeWriter.VisitStartingForTest` runs at the
   start of a visit, before the write lock, which is the window the tests use.
2. **Ruling C5-Q1.** `DecideLocked`: a `Processing` pipe that wrote nothing since the last visit writes `Heartbeat`
   while `now - Since < ProcessingLimit`, and `Stalled` past it. `ChannelOperationKind.Processing`'s doc comment and the
   `Visit` summary state this. Not edited: the spec (section 2.2) and the plan still say only Idle, WaitingOnVolume
   and Queued heartbeat; the rulings table (C5-Q1) is the amendment, and I left the orchestrator's documents to the
   orchestrator.
3. **Ruling C5-Q2**: no change (the S2 skip stands).
4. `WedgedProcessing_WritesStalledNamingStepAndCloses` now reads heartbeats too, because a processing pipe heartbeats
   within the limit: it asserts that Stalled is the last frame and that the non-heartbeat frames are exactly
   `[Cursor, Stalled]`.

New tests (`MFTLib.Tests/JournalBrokerHostLivenessTests.Revalidation.cs`):
- `Visit_ProgressPublishedAfterVisitBegan_WritesNoStalledAndKeepsChannel`: a `Processing` pipe exactly
  `ProcessingLimit` old; a visit starts on another thread and parks on a `TestGate` in the window; the test republishes
  the step; the visit writes `[Heartbeat]`, and the owner is not cancelled.
- `Visit_FrameCompletedAfterVisitBegan_WritesNoStalledAndKeepsChannel`: same, but the window action completes a
  `CaughtUp` frame; the pipe carries only `[CaughtUp]`, and the owner is not cancelled.
- `ProgressingCatchUp_NoFrameForSixtySeconds_HeartbeatsAndNeverStalls`: six bounded catch-up reads of 10 s each (each
  held on a `TestGate` across two intervals) after `ScanReady`; every one of the 12 intervals delivers a `Heartbeat`,
  no `Stalled`, and the terminal batch holds all 6 entries. The source's gates are released in a `finally`, so a failed
  assertion is reported instead of being masked by harness disposal.

### RED evidence (W40-R1), rerun for every new test in this task

Each block is one uncommitted scratch mutation (script `.superpowers/mutations2.py`, git-ignored; every mutation was
restored and a clean rebuild confirmed, "restored; clean build exit 0"). Output copied from
`.superpowers/mutations2.log`, `mutations2b.log`, `mutations2c.log` and `mutations2d.log`. For the tests that fail
through the harness's 10 s read guard, the stack frames (from `mutations2c.log`/`mutations2d.log`) show the test line
that waited for the missing frame.

```
===== M3 republish of the same step keeps the old Since | build exit 0
COMMAND: dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostLivenessTests.ProcessingWithProgress_NeverStalls"
    Failed ProcessingWithProgress_NeverStalls [52 ms]
        Assert.IsFalse failed.
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 61 ms - MFTLib.Tests.dll (net10.0)
===== M4 heartbeat starts even with a write in flight | build exit 0
COMMAND: dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostLivenessTests.BlockedWriteOnX_DoesNotDelayHeartbeatsOnY_OnlyXFaults|FullyQualifiedName~JournalBrokerHostLivenessTests.HeartbeatSkipped_WhilePreviousWriteInFlight"
    Failed BlockedWriteOnX_DoesNotDelayHeartbeatsOnY_OnlyXFaults [49 ms]
        Assert.AreEqual failed. Expected:<1>. Actual:<10>. X's first heartbeat is held, and no later heartbeat is started on X.
    Failed HeartbeatSkipped_WhilePreviousWriteInFlight [26 ms]
        Assert.AreEqual failed. Expected:<1>. Actual:<3>. Three visits to a pipe with a held write start exactly one heartbeat.
    Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 94 ms - MFTLib.Tests.dll (net10.0)
===== M5 sender on the thread pool | build exit 0
COMMAND: dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostLivenessTests.HeartbeatSender_RunsOnDedicatedThread"
    Failed HeartbeatSender_RunsOnDedicatedThread [26 ms]
        Assert.IsFalse failed. The sender must not run on the thread pool.
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 35 ms - MFTLib.Tests.dll (net10.0)
===== M6 catch-up republishes before every call | build exit 0
COMMAND: dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostLivenessTests.CatchUp_BoundedReads_RepublishesPerCall|FullyQualifiedName~JournalBrokerHostLivenessTests.CatchUp_SecondBoundedReadFails_WritesCatchUpLostAndNoBatch"
    Failed CatchUp_BoundedReads_RepublishesPerCall [61 ms]
        Assert.AreEqual failed. Expected:<3>. Actual:<7>. The progress clock restarts once per call that returned a chunk.
    Failed CatchUp_SecondBoundedReadFails_WritesCatchUpLostAndNoBatch [6 ms]
        Assert.AreEqual failed. Expected:<1>. Actual:<3>. Only the call that returned a chunk restarted the progress clock.
    Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 81 ms - MFTLib.Tests.dll (net10.0)
===== M6b catch-up retries a failed call | build exit 0
COMMAND: dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostLivenessTests.CatchUp_SecondBoundedReadFails_WritesCatchUpLostAndNoBatch"
    Failed CatchUp_SecondBoundedReadFails_WritesCatchUpLostAndNoBatch [82 ms]
        Assert.AreEqual failed. Expected:<2>. Actual:<3>. A failed bounded read is not retried.
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 96 ms - MFTLib.Tests.dll (net10.0)
===== M7 flush reports nothing | build exit 0
COMMAND: dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostLivenessTests.BlockFlush_EachRange_RestartsScanPipeProgressClock"
    Failed BlockFlush_EachRange_RestartsScanPipeProgressClock [64 ms]
        Assert.AreEqual failed. Expected:<3>. Actual:<0>. Each flushed range republishes the scan pipe's state.
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 75 ms - MFTLib.Tests.dll (net10.0)
===== M8 visit decides from a snapshot taken when it began (the reviewed race) | build exit 0
COMMAND: dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostLivenessTests.Visit_ProgressPublishedAfterVisitBegan_WritesNoStalledAndKeepsChannel|FullyQualifiedName~JournalBrokerHostLivenessTests.Visit_FrameCompletedAfterVisitBegan_WritesNoStalledAndKeepsChannel"
    Failed Visit_ProgressPublishedAfterVisitBegan_WritesNoStalledAndKeepsChannel [45 ms]
        CollectionAssert.AreEqual failed. Progress published inside the visit's window restarts the clock: a heartbeat, not Stalled.(Element at index 0 do not match.)
    Failed Visit_FrameCompletedAfterVisitBegan_WritesNoStalledAndKeepsChannel [18 ms]
        CollectionAssert.AreEqual failed. A frame completed inside the visit's window counts as this interval's write: no Stalled.(Different number of elements.)
    Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 80 ms - MFTLib.Tests.dll (net10.0)
COMMAND: dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostLivenessTests.ProgressingCatchUp_NoFrameForSixtySeconds_HeartbeatsAndNeverStalls"
    Failed ProgressingCatchUp_NoFrameForSixtySeconds_HeartbeatsAndNeverStalls [20 s]
        Test method MFTLib.Tests.JournalBrokerHostLivenessTests.ProgressingCatchUp_NoFrameForSixtySeconds_HeartbeatsAndNeverStalls threw exception:
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 20 s - MFTLib.Tests.dll (net10.0)

===== M1 no heartbeat writes | build exit 0
COMMAND: dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostLivenessTests.IdleWatch_WaitingOnVolume_HeartbeatsAndNeverStalls|FullyQualifiedName~JournalBrokerHostLivenessTests.QueuedScan_WaitingForAdmission_Heartbeats|FullyQualifiedName~JournalBrokerHostLivenessTests.IdleSession_NoRequestsNoWatches_StaysAlivePastStallLimit|FullyQualifiedName~JournalBrokerHostLivenessTests.BlockedWriteOnX_DoesNotDelayHeartbeatsOnY_OnlyXFaults|FullyQualifiedName~JournalBrokerHostLivenessTests.HeartbeatSkipped_WhilePreviousWriteInFlight"
System.OperationCanceledException: The operation was canceled.
      at System.Threading.CancellationToken.ThrowOperationCanceledException()
   at MFTLib.Tests.JournalBrokerHostLivenessTests.ReadIncludingHeartbeatsAsync(Stream pipe) in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.cs:line 175
   at MFTLib.Tests.JournalBrokerHostLivenessTests.IdleWatch_WaitingOnVolume_HeartbeatsAndNeverStalls() in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.cs:line 34
   at MFTLib.Tests.JournalBrokerHostLivenessTests.IdleWatch_WaitingOnVolume_HeartbeatsAndNeverStalls() in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.cs:line 39
System.OperationCanceledException: The operation was canceled.
      at System.Threading.CancellationToken.ThrowOperationCanceledException()
   at MFTLib.Tests.JournalBrokerHostLivenessTests.ReadIncludingHeartbeatsAsync(Stream pipe) in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.cs:line 175
   at MFTLib.Tests.JournalBrokerHostLivenessTests.QueuedScan_WaitingForAdmission_Heartbeats() in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.cs:line 138
   at MFTLib.Tests.JournalBrokerHostLivenessTests.QueuedScan_WaitingForAdmission_Heartbeats() in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.cs:line 135
System.OperationCanceledException: The operation was canceled.
      at System.Threading.CancellationToken.ThrowOperationCanceledException()
   at MFTLib.Tests.JournalBrokerHostLivenessTests.IdleSession_NoRequestsNoWatches_StaysAlivePastStallLimit() in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.cs:line 152
   at MFTLib.Tests.JournalBrokerHostLivenessTests.IdleSession_NoRequestsNoWatches_StaysAlivePastStallLimit() in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.cs:line 156
System.OperationCanceledException: The operation was canceled.
      at System.Threading.CancellationToken.ThrowOperationCanceledException()
   at MFTLib.Tests.JournalBrokerHostLivenessTests.ReadIncludingHeartbeatsAsync(Stream pipe) in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.cs:line 175
   at MFTLib.Tests.JournalBrokerHostLivenessTests.BlockedWriteOnX_DoesNotDelayHeartbeatsOnY_OnlyXFaults() in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.Progress.cs:line 26
   at MFTLib.Tests.JournalBrokerHostLivenessTests.BlockedWriteOnX_DoesNotDelayHeartbeatsOnY_OnlyXFaults() in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.Progress.cs:line 31
     at MFTLib.Tests.JournalBrokerHostLivenessTests.HeartbeatSkipped_WhilePreviousWriteInFlight() in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.Progress.cs:line 44
   at MFTLib.Tests.JournalBrokerHostLivenessTests.HeartbeatSkipped_WhilePreviousWriteInFlight() in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.Progress.cs:line 48
    Failed IdleWatch_WaitingOnVolume_HeartbeatsAndNeverStalls [10 s]
        System.OperationCanceledException: The operation was canceled.
    Failed QueuedScan_WaitingForAdmission_Heartbeats [10 s]
        System.OperationCanceledException: The operation was canceled.
    Failed IdleSession_NoRequestsNoWatches_StaysAlivePastStallLimit [10 s]
        System.OperationCanceledException: The operation was canceled.
    Failed BlockedWriteOnX_DoesNotDelayHeartbeatsOnY_OnlyXFaults [10 s]
        System.OperationCanceledException: The operation was canceled.
    Failed HeartbeatSkipped_WhilePreviousWriteInFlight [2 ms]
        Assert.AreEqual failed. Expected:<1>. Actual:<0>. The first visit starts a heartbeat, which is held.
    Failed!  - Failed:     5, Passed:     0, Skipped:     0, Total:     5, Duration: 40 s - MFTLib.Tests.dll (net10.0)
===== M2 no Stalled write | build exit 0
COMMAND: dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostLivenessTests.WedgedProcessing_WritesStalledNamingStepAndCloses"
     at MFTLib.Tests.JournalBrokerHostLivenessTests.WedgedProcessing_WritesStalledNamingStepAndCloses() in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.cs:line 69
   at MFTLib.Tests.JournalBrokerHostLivenessTests.WedgedProcessing_WritesStalledNamingStepAndCloses() in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.cs:line 73
    Failed WedgedProcessing_WritesStalledNamingStepAndCloses [10 s]
        Assert.AreEqual failed. Expected:<Stalled>. Actual:<JournalBatch>. Stalled is the last frame, then EOF.
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 10 s - MFTLib.Tests.dll (net10.0)
===== M9 a progressing Processing pipe writes nothing (before ruling C5-Q1) | build exit 0
COMMAND: dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostLivenessTests.ProgressingCatchUp_NoFrameForSixtySeconds_HeartbeatsAndNeverStalls"
System.OperationCanceledException: The operation was canceled.
      at System.Threading.CancellationToken.ThrowOperationCanceledException()
   at MFTLib.Tests.TestSupport.HostChannelHarness.ReadAnyFrameAsync(Stream stream) in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\TestSupport\HostChannelHarness.cs:line 132
   at MFTLib.Tests.JournalBrokerHostLivenessTests.ReadIncludingHeartbeatsAsync(Stream pipe) in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.cs:line 175
   at MFTLib.Tests.JournalBrokerHostLivenessTests.AssertCatchUpHeartbeatsAsync(HostChannelHarness harness, Liveness liveness, TestGate[] calls) in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.Revalidation.cs:line 104
   at MFTLib.Tests.JournalBrokerHostLivenessTests.ProgressingCatchUp_NoFrameForSixtySeconds_HeartbeatsAndNeverStalls() in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.Revalidation.cs:line 75
   at MFTLib.Tests.JournalBrokerHostLivenessTests.ProgressingCatchUp_NoFrameForSixtySeconds_HeartbeatsAndNeverStalls() in C:\Users\mtsch\MFTLib-worktrees\265-C5\MFTLib.Tests\JournalBrokerHostLivenessTests.Revalidation.cs:line 80
    Failed ProgressingCatchUp_NoFrameForSixtySeconds_HeartbeatsAndNeverStalls [10 s]
        System.OperationCanceledException: The operation was canceled.
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 10 s - MFTLib.Tests.dll (net10.0)
```

Mutations: M1 visits write no heartbeat; M2 no `Stalled`; M3 a same-step republish keeps the old `Since`; M4 a
heartbeat starts even with a write in flight; M5 the sender loop runs on the thread pool; M6 catch-up republishes
before every call; M6b a failed catch-up call is retried; M7 the flush reporter is dropped; M8 the visit decides from a
state and flag snapshot taken when it began (the reviewed race); M9 a Processing pipe within the limit writes nothing
(before ruling C5-Q1). M1, M2 and M9 were run a second time only to capture the exception type and stack (the first
run printed just "threw exception:"); M9's first run also failed but in harness disposal (TimeoutException), which is
why the test now releases its gates in a `finally`, and its rerun above shows the heartbeat read failing.

GREEN:
`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~JournalBrokerHostLivenessTests"`
-> `Passed!  - Failed: 0, Passed: 14, Skipped: 0, Total: 14`.
`--filter "FullyQualifiedName~JournalBrokerHostLivenessTests|FullyQualifiedName~JournalBrokerHostSourcesTests"` ->
`Passed!  - Failed: 0, Passed: 19, Skipped: 0, Total: 19`.

### Whole suite and aislop

`.\scripts\run-coverage.ps1 -NonInteractive`: `Total tests: 1667  Passed: 1661  Skipped: 6`, 0 failed, exit 0; line
coverage 97.4%, `HostPipeWriter` 97.8%.
`aislop scan . -d`: `99 / 100  Healthy  0 errors · 5 warnings`, the same gate as before: the four baseline warnings
(`NativeSeamIsolationFixtures.cs:73`, `:79`; `CachedBlockDeletionOutcome.cs:8`, `:10`) and the ruled 8-parameter
`JournalBrokerHost` constructor.

### Threads and locks (amended)

A visit acquires the pipe's write lock with `Wait(0)` and only then takes `_gate` to decide; lock order is write lock,
then `_gate`, the same order `WriteFrameAsync` uses, so there is no inversion. The sender still never waits: `Wait(0)`
never blocks, and the write it starts completes on its own and releases the lock.

Primary checkout after fix round 1: `git -C C:\Users\mtsch\MFTLib status --short` printed nothing.

Note on the RED block: the unlabeled `ProgressingCatchUp...` entry right after M8 is M9's first run (20 s, the
disposal TimeoutException described above). M2 failed in both runs but by two paths that race: in the first run the
test's frame read hit its 10 s guard ("threw exception: System.OperationCanceledException", `mutations2.log`); in the
stack-capture rerun (`mutations2d.log`) the source's own 10 s hang guard expired first, the scan completed, and the
test's assertion reported `Expected:<Stalled>. Actual:<JournalBatch>`. Both are the missing Stalled.
