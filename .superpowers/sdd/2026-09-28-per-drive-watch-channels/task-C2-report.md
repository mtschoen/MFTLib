# Task C2 report: BrokerProcess client, scan channels, BrokerTestHarness

Status: DONE_WITH_CONCERNS (concerns are interpretation calls, listed at the end; everything is green)

Worktree `C:\Users\mtsch\MFTLib-worktrees\265-C2`, branch `task/265-C2`, base `cb13c86` confirmed.
Commit: `054fbb3` "BrokerProcess owns the control pipe and scans each drive on its own channel".

## What was implemented

Production (`MFTLib`):
- `Broker/Client/BrokerProcess.cs` (fields, `HasEnded`, `Ended`, idempotent `DisposeAsync`, `RequestEnd`),
  `.Control.cs` (request ids, serialized control writes, control reader, `QueryVolumeAsync`,
  `GrowUsnJournalAsync`), `.Channels.cs` (`OpenChannelAsync`), `.Scan.cs` (`ScanDriveAsync` and the
  one-drive frame collector), `.Launch.cs` (`LaunchAsync` x2, `_connectTimeout` seam, real named sections).
- `BrokerDriveChannel.cs` (owns listener/pipe, reader, tag; `WriteAsync`, `ReadAsync`, idempotent dispose),
  `BrokerFrameReader.cs`, `BrokerChannelLostException.cs`, `BrokerDriveScanResult.cs`,
  `BrokerBlockSectionFactory.cs`, `BrokerPipes.cs` (`IBrokerPipeFactory`, `BrokerPipeListener`,
  `NamedPipeBrokerPipeFactory`), `BrokerMftBlockProducer.cs` (scan half; `_connectAsync` is
  `Func<CancellationToken, Task<BrokerProcess>>`; copies `CatchUpLoss`).
- Restored in the spec shapes because the new code reads them: `BlockScanTarget.cs`, `BlockScanOutcome.cs`,
  `BrokerProgressAdapter.cs`. `BlockFile.DeleteOnClose` NOT restored (nothing reads it; the target's
  `DeleteOnClose` feeds `BlockFileCreateOptions.DeleteOnClose`). `BrokerScanOptions` already had no
  `BlockTargets`; `MftBlockCapacity` unchanged (its signature never named the client).
- `Index/MftBlockProducer.cs`: `MftBlockProduceResult.CatchUpLoss { get; init; }`.
- `Broker/BrokerLiveness.cs` (ruling R-W4-1): `HeartbeatInterval` 5 s, `ProcessingLimit` 30 s,
  `StallLimit` 30 s, `ControlReplyTimeout` 30 s, each a `TimeSpan` with a doc comment naming what enforces
  it. `BrokerProcess.ControlReplyTimeout` reads `BrokerLiveness.ControlReplyTimeout`.
- Frame-length defect (assigned): new `Broker/Protocol/BrokerFrameStream.cs` is the one frame reader for
  host and client; `JournalBrokerHost.Frames.cs` `ReadFrameAsync` now forwards to it (the host's
  duplicate `ReadExactAsync` is deleted). A prefix `< 1` or `> MaximumFrameLength` (1 GiB, `1 << 30`)
  throws `InvalidDataException` before any allocation. Reasoning is in the constant's comment: the
  largest legitimate frame is a scan's terminal `JournalBatch`; a wire entry (46 + name bytes) is smaller
  than its native `USN_RECORD_V2` (60 + name, 8-aligned), so a batch is smaller than the journal span it
  covers, which NTFS trims under MaximumSize + AllocationDelta; Windows defaults to 32 MB, servers
  commonly 512 MB (web search found no documented hard cap on MaximumSize); 1 GiB is twice the server
  size and stays far below `Array.MaxLength`. Host effect: control pipe -> `ServeAsync` ends with
  `InvalidDataException` (the existing "malformed control frame ends the session" path); drive pipe ->
  existing first-request `Error` "malformed" frame and only that channel ends. Client effect:
  `BrokerChannelLostException` (control: process ends, pending fail, `Ended` fires; drive: the scan fails
  naming the drive).

Test extensions (`MFTLibTestExtensions`):
- `BrokerTestHarness.cs` (both public `StartInProcess` overloads, plus internal `Start` with two extra
  seams for MFTLib's own tests: wrap each client end by pipe name, wrap the host connector),
  `BrokerTestHarnessOptions.cs`, `InMemoryBrokerPipes.cs` (implements `IBrokerPipeFactory` and the host's
  `BrokerChannelConnector` by pipe name over `System.IO.Pipelines`), `InMemoryDuplexStream.cs` (also
  `HeldWriteStream`, `HostLifetimeStream`), `DelegatingStream.cs`.
- csproj: `InternalsVisibleTo MFTLib.Tests` (so tests reach `InMemoryBrokerPipes` and the internal start).

Behavior decisions within the brief:
- Request ids (R12): allocated under the process lock; skip 0 and every table entry; full table throws
  `InvalidOperationException("No broker request id is free")`; entries leave only on reply or process end
  (a cancellation before the frame starts writing releases the id, per spec 2.2).
  `StartingRequestIdForTest` is read once, by the next allocation after it is set, as the id last issued
  (this is what makes the brief's scenario "id 1 still pending, next ids MaxValue then 2" expressible).
  Added `internal int PendingRequestCountForTest` for `RequestIds_ReleasedOnProcessEnd`.
- Control writes (R6): the caller's token is observed until the write lock is held; once the frame starts,
  it is written under `_lifetime` linked with a `ControlReplyTimeout` timer on the injected clock and the
  caller waits for the write to finish (strict reading of "observed only before its frame starts
  writing"); the reply wait then observes the caller's token (late-reply rule). A failed or timed-out
  write records the reason and cancels the process lifetime; the control reader then ends the process
  (so `Ended` fires from the reader, once), fails every pending request with
  `BrokerChannelLostException(null, ...)` and closes the control pipe.
- `OpenChannelAsync` (R5): listener, request, connection and `ChannelOpened` in either order, then the
  first request via `BrokerDriveChannel.WriteAsync`. Every failure branch cancels the connection wait and
  disposes the listener, which releases the connected stream too (both factories). A connection that
  never follows `ChannelOpened` fails after `ControlReplyTimeout` with `BrokerChannelLostException(drive)`.
  Control loss is rewrapped as `BrokerChannelLostException(drive, ...)`. A channel opened while the
  process is disposing fails instead of escaping disposal's sweep.
- `ScanDriveAsync`: order Cursor, ScanProgress*, ScanReady, terminal (JournalBatch | CatchUpLost), then EOF;
  `Heartbeat` skipped anywhere; anything else, or EOF early, is `BrokerChannelLostException(drive)`;
  `Error` is `InvalidOperationException(message)` and disposes lifetime and block. The lifetime is
  disposed at ScanReady (unpublish) as before. Progress samples get the channel's drive letter. A control
  loss during the scan's volume query is rewrapped to name the drive.
- `HoldWrites` (harness) holds HOST writes to the named pipe, as the option's doc in the spec says; the
  two `ControlWrite_*` tests hold/fail the CLIENT's control write through the internal client-stream seam
  (`SplitFrameWrite` in TestSupport), which is how I read the brief's "through an in-memory stream
  wrapper". `HarnessHoldWrites_HoldsHostControlReplyUntilReleased` covers the option itself.
- Harness disposal: `HostLifetimeStream.DisposeAsync` closes the client control end, awaits
  `host.ServeAsync`, then releases the host end; a host session that faulted surfaces from the process's
  first `DisposeAsync` (only `ControlWrite_FailsMidFrame` hits this and asserts it).

## TDD evidence

RED 1 (all brief-named tests against a stub `BrokerProcess` whose members threw `NotImplementedException`):
`dotnet test ... --filter "FullyQualifiedName~BrokerProcessTests|FullyQualifiedName~BrokerProcessLaunchTests"`
```
Failed!  - Failed:    39, Passed:     2, Skipped:     0, Total:    41
```
Every one of the 39 failures is the stub's `NotImplementedException` (thrown directly, or inside the
assertion's "threw X but Y was expected" message for the `ThrowsExceptionAsync` cases); the two passes
were the dispose tests on a no-op stub dispose. Expected: no
behavior existed.

RED 2 (frame length, with the real client and host but before the length check was added):
same command plus `FullyQualifiedName~BrokerFrameLengthTests`:
```
Failed Host_ControlFrameLengthBeyondMaximum_EndsSessionWithInvalidData (2147483647)  Threw exception OverflowException, but exception InvalidDataException was expected. at BrokerFrameStream.ReadFrameAsync line 38
Failed Host_ControlFrameLengthBeyondMaximum_EndsSessionWithInvalidData (1073741825)  [10 s: allocated the frame and waited for bytes]
Failed Host_DriveFirstRequestLengthBeyondMaximum_WritesErrorAndOtherRequestsContinue  System.OverflowException
Failed Client_ControlFrameLengthBeyondMaximum_EndsProcessWithChannelLost  System.OverflowException
Failed Client_DriveFrameLengthBeyondMaximum_ScanFailsWithChannelLost  Threw exception OverflowException, but exception BrokerChannelLostException was expected.
```
Exactly the recorded defect (overflow / huge allocation instead of `InvalidDataException`).

RED 3 (tests I added for behavior beyond the named list, by scratch mutation, not committed):
- `ControlWrite_NeverFinishes_EndsProcessAfterReplyTimeout`, `OpenChannel_AcknowledgedButNeverConnected_TimesOutAndReleasesPipe`
  with both `ControlReplyTimeout` bounds replaced by `Timeout.InfiniteTimeSpan`: both failed at the 10 s
  hang guard (`OperationCanceledException`).
- `ScanDrive_ProcessEndsBeforeChannelOpens_FailsNamingDrive` with the rewrap disabled:
  `Assert.AreEqual failed. Expected:<C>. Actual:<(null)>.`
- `ScanDrive_ForwardsProfileAndKeepFileNames` with keep names not forwarded:
  `CollectionAssert.AreEqual failed. (Different number of elements.)`

GREEN (final, after every fix):
`dotnet test MFTLib.Tests\MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~BrokerProcessTests|FullyQualifiedName~BrokerProcessLaunchTests|FullyQualifiedName~BrokerFrameLengthTests|FullyQualifiedName~JournalBrokerHostChannelTests"`
```
Passed!  - Failed:     0, Passed:    89, Skipped:     0, Total:    89
```
Run three times in a row green before the last test edits, once more after them. `NativeSeamIsolation`
and `NamespaceBoundary` were also run green with the new classes.

## Whole suite, coverage, aislop

- `.\scripts\run-coverage.ps1 -NonInteractive` (last run before the final two test-only edits: a
  redundant type argument removed and an `Inconclusive` guard for non-Windows in the end-to-end launch
  test; both re-run green in the targeted set above):
  `Total tests: 1356  Passed: 1350  Skipped: 6` (0 failed), `Line coverage: 96.1%`.
  New types: BrokerProcess 92.8%, BrokerFrameReader 100%, BrokerDriveChannel 96%, BrokerPipeListener 100%,
  NamedPipeBrokerPipeFactory 100%, BrokerFrameStream 92.3% (first run numbers).
- `aislop scan .` (0.16.0): `99 / 100`, 0 errors, 6 warnings, all six present on the untouched base
  `cb13c86` (I scanned an extracted copy of the base to confirm, since lane-common's list of four predates
  C1): `NativeSeamIsolationFixtures.cs:73`, `:79` (AsyncFixer01), `CachedBlockDeletionOutcome.cs:8`, `:10`
  (redundant doc), `DriveStatus.cs:42` (`CompactionNeeded.get` never used), `JournalBrokerHost.cs:43`
  (8 parameters; C1 ruling W2-2). No finding in any file this task touched. The three unread
  `BrokerLiveness` members were not flagged.
- `RCS1194` on `BrokerChannelLostException` is suppressed with a justification, as the repo does for
  `DriveWatchFaultException` and `ClientDisconnectedException` (the spec fixes the one constructor).

## Files changed

Modified: `MFTLib/Broker/Host/JournalBrokerHost.Frames.cs`, `MFTLib/Index/MftBlockProducer.cs`,
`MFTLibTestExtensions/MFTLibTestExtensions.csproj`.
Created (MFTLib): `Broker/BrokerLiveness.cs`, `Broker/Protocol/BrokerFrameStream.cs`,
`Broker/Client/{BrokerProcess, BrokerProcess.Control, BrokerProcess.Channels, BrokerProcess.Scan,
BrokerProcess.Launch, BrokerDriveChannel, BrokerFrameReader, BrokerChannelLostException,
BrokerDriveScanResult, BrokerBlockSectionFactory, BrokerPipes, BrokerMftBlockProducer,
BrokerProgressAdapter, BlockScanTarget, BlockScanOutcome}.cs`.
Created (MFTLibTestExtensions): `BrokerTestHarness.cs`, `BrokerTestHarnessOptions.cs`,
`InMemoryBrokerPipes.cs`, `InMemoryDuplexStream.cs`, `DelegatingStream.cs`.
Created (MFTLib.Tests): `BrokerProcessTests.cs`, `.Channels.cs`, `.Scan.cs`, `.Timeouts.cs`,
`BrokerProcessLaunchTests.cs`, `BrokerFrameLengthTests.cs`, `TestSupport/InProcessBroker.cs`,
`TestSupport/ScriptedBroker.cs`, `TestSupport/BrokerTestStreams.cs`.
`HostChannelHarness.cs`: NOT changed (no members added; the frame-length host tests only call its
existing public members).

## Self-review, concerns, adjacent

1. ControlReplyTimeout scope. My brief's R5/R6 bullets name two bounds on `ControlReplyTimeout` (a started
   control write; a connection after `ChannelOpened`), while its interface comment says "enforced by C7"
   and the dispatch says reply timeouts go to C7. C7's brief lists only the reply wait and the stall
   limit, so I built the two R5/R6 bounds (with tests) and did NOT build the per-request reply wait or the
   stall limit. C7 adds the reply wait in `RequestAsync` (`reply.Task.WaitAsync`) and the stall limit in
   `BrokerFrameReader`.
2. `BrokerLiveness` contents follow the dispatch (four `TimeSpan`s incl. `ControlReplyTimeout`). The
   orchestrator-rulings file's W4-1 row says "all four constants exactly as C5's brief declares them",
   which includes `CatchUpBufferReadsPerCall` (an int) instead of `ControlReplyTimeout`. I followed the
   dispatch; C5 adds `CatchUpBufferReadsPerCall` itself.
3. `HoldWrites` semantics (host writes only) versus the brief's control-write test wording; see above.
4. `_lifetime` (a never-linked, timer-less `CancellationTokenSource`) is deliberately not disposed, with a
   comment: a control write that fails after disposal still cancels it.
5. The control reader converts any exception into the end reason (so it never faults unobserved); the
   case that needed it is an in-memory pipe that throws `InvalidOperationException` on read after close.
6. Harness disposal rethrows a host session fault. Consumers disposing after a malformed control exchange
   would see it; I judged surfacing better than hiding. Flagging it in case the orchestrator prefers the
   harness to swallow host faults.
7. Adjacent, not fixed: AGENTS.md ("VolumeBroker" bullet, "MFTLibTestExtensions" bullet) and
   `docs/broker-*.md` still describe `JournalBrokerClient`/`ScanSessionTestHarness`; spec section 4 assigns
   those rewrites to the docs task. `BrokerMftBlockProducer` is at ~81% line coverage (the validation
   failure branches); C4 ports `BrokerMftBlockProducerTests`. `MFTLibTestExtensions.DelegatingStream`'s
   synchronous members are uncovered.
8. `scripts/coverage-linux.sh` (C3b's) is untouched; `LaunchAsync_EndToEnd_...` returns Inconclusive off
   Windows as the old test did. Linux was not built in this lane.

## Fix round 1

Status: DONE. All three findings are fixed. Commit `3541d3e` on task/265-C2, on top of `054fbb3`.
`30b45b2` was the first version of this commit, made while finding 1 was still open; I amended it after
the ruling, so that SHA is gone.

### Finding 1: harness disposal (owner ruling: mirror production)
The ruling on how a host fault reaches a test came from the owner, relayed by the lead. It replaces the
options A to D I had proposed: the harness has no fault surface of its own, and a host fault reaches a
test exactly as it reaches production code.

What changed:
- `BrokerProcess.DisposeAsync` always finishes its teardown: control pipe, drive channels, control
  reader. It never throws. If the control pipe fails to close, that is logged on the control channel.
- When the in-process host's `ServeAsync` ends for any reason (returns, faults or is cancelled),
  `BrokerTestHarness` runs `ExitHost` as a continuation:
  - a faulted session is logged with `BrokerDiagnostics.Log` on the control channel, as
    `Broker session failed: {exception}`. Reading the exception also means the faulted task is observed;
  - a cancelled session is logged;
  - `InMemoryBrokerPipes.CloseHostEnds` (backed by the new `HostPipeEnds`) then closes every pipe end the
    host held, control and drive, as a process exit does. A connection attempted after the exit fails.
- `HostLifetimeStream` waits for that exit, which never faults. No new public member was added, and the
  `StartInProcess` signatures are unchanged.
- Test support: `CorruptFrameWrite` (in `TestSupport/BrokerTestStreams.cs`) replaces the kind byte of one
  chosen client control write, so the host's session fails with `InvalidDataException`.

Tests in `BrokerProcessTests.HostFault.cs`. All four failed first against the code before this change
(log: `.superpowers/fix1-red-f1.log`):
- `HostFault_WithPendingRequest_FailsRequestEndsProcessOnceAndDisposesQuietly`.
  - RED: the request never ended, so `ThrowsExceptionAsync` reported `TimeoutException` where
    `BrokerChannelLostException` was expected. The host's ends stayed open.
- `HostFault_WithOpenScanChannel_FailsScanWithChannelLost`.
  - RED: the same `TimeoutException` where `BrokerChannelLostException` was expected.
  - GREEN: the scan fails with `BrokerChannelLostException('C')`, never `InvalidOperationException`; the
    pending query fails with `BrokerChannelLostException`; `HasEnded` is true; the section is released.
- `HostFault_TestBodyThrowsInsideAwaitUsing_BodyExceptionEscapes`.
  - RED: `Threw exception InvalidDataException, but exception InvalidTimeZoneException was expected`,
    meaning the host fault masked the test body's exception.
- `HostFault_ExceptionMessageIsWrittenToDiagnosticsLog`.
  - RED: `InvalidDataException: Unknown frame kind: 200` was thrown out of disposal, and nothing was logged.
  - GREEN: after `BrokerDiagnostics.FlushAsync`, a `[...:control]` line contains
    "Unknown frame kind: 200".

Tests changed to follow the ruling:
- `Dispose_ControlCloseThrows_StillClosesChannelsAndJoinsReaderFirst` is now
  `Dispose_ControlCloseThrows_CompletesTeardownWithoutThrowing`.
  - Before the ruling it expected the close failure to be rethrown after teardown.
  - RED against the rethrowing version: `System.IO.IOException: control close failed`.
  - Its first form had been RED against the original code with "The control reader was joined before
    disposal threw" (`.superpowers/fix1-red1.log`).
- `ControlWrite_FailsMidFrame_EndsProcessLoudly` previously asserted that disposal throws
  `EndOfStreamException`. It now asserts that disposal completes.

### Finding 2: timeout tests on the injected clock
- New `TestSupport/TimerSignalingClock` (a `FakeTimeProvider`). It signals each timer it creates, keyed by
  due time and by creation order, and records whether the timer fired.
- Both tests assert `BrokerLiveness.ControlReplyTimeout == 30 s` and that `BrokerProcess.ControlReplyTimeout`
  equals it. Each test then:
  1. waits for the timer due at exactly that limit. The connection bound is the second such timer; the
     first bounds the OpenChannel write.
  2. advances the clock to one tick short of the limit and asserts the timer has not fired and there is no
     outcome.
  3. advances one more tick and asserts the timer fired, then checks the outcome.
- No `Task.Delay` and no stepping loop. The old `AdvanceUntilCompleteAsync` is deleted.
- RED by scratch mutation, not committed (the edit was not denied): write bound set to 60 s, connection
  bound to 29 s. Both tests failed with `TimeoutException` at the 10 s hang guard, because no timer due
  at 30 s ever appeared. Log: `.superpowers/fix1-red2.log`.

### Finding 3: disposal racing an in-flight operation
New tests in `BrokerProcessTests.Disposal.cs`:
- `Dispose_DuringScan_FailsScanWithChannelLostAndReleasesSection`: the host ends the channel. Passed on the
  first run, because the harness awaits the host, which closes the drive pipe first.
- `Dispose_DuringScanTheHostAbandons_FailsScanWithChannelLostAndReleasesSection`: the scan source ignores
  cancellation. The host clock (a `TimerSignalingClock`) is advanced past `ControlClosedGracePeriod` only
  once the drain timer exists, so the client's own sweep closes the pipe under the pending read.
  - RED: the scan never completed, `TimeoutException` at the hang guard. A pending read on a closed
    in-memory pipe never returns.
  - Production risk behind it: disposing a named pipe under a pending read can surface an
    `OperationCanceledException` the caller did not ask for.
  - Fix: `BrokerDriveChannel` has a `_closing` token that is cancelled on dispose and linked into every
    read. A read cut off by the channel's own disposal throws `BrokerChannelLostException(drive, ...)`.
    GREEN.
- `HarnessPipe_AfterClose_ReadAndWriteThrowObjectDisposed`: this confirms the review's suspicion.
  - RED: `Threw exception InvalidOperationException, but exception ObjectDisposedException was expected.`
  - Fix: `InMemoryDuplexStream` throws `ObjectDisposedException` on any read, write or flush after close,
    matching named pipes. GREEN.
- `Dispose_DuringChannelOpen_FailsOpenWithChannelLostAndClosesPipe`: holds the open in the new internal
  `BrokerProcess.BeforeChannelTrackedForTest` hook, which runs between connection and registration.
  Disposes the process, then releases. Asserts `BrokerChannelLostException('C')` and that the host's end is
  disposed. This covers `TrackChannel`'s disposed branch, which already existed, so the test passed on its
  first run; no defect.

### Also fixed, found by these runs
`ControlWrite_CancelledMidFrame_FinishesFrame_NextRequestSucceeds` failed in class runs, not on its own.
- The cause: when the caller cancelled during its frame's write, `RequestAsync` could still return the
  reply if the reply arrived before the continuation ran.
- The fix is one line: after the write, `cancellationToken.ThrowIfCancellationRequested()`. The request
  entry stays until its reply is dropped, per the late-reply rule.
- Afterwards the targeted classes passed 5 runs in a row, then 3 more after the teardown fix.

### Verification (final code)
- Targeted:
  `dotnet test ... --filter "FullyQualifiedName~BrokerProcessTests|FullyQualifiedName~BrokerProcessLaunchTests|FullyQualifiedName~BrokerFrameLengthTests|FullyQualifiedName~JournalBrokerHostChannelTests"`
  gives `Passed! - Failed: 0, Passed: 98, Total: 98`. That was three runs in a row after the finding-1
  change, and once more after the `HostPipeEnds` extraction.
- `run-coverage.ps1 -NonInteractive` on the committed code: `Total tests: 1365, Passed: 1359, Skipped: 6`
  (0 failed); line coverage 96%.
- `aislop scan .`: `99 / 100`, 6 warnings, all the same ones as on base `cb13c86`.
  - An intermediate scan flagged `InMemoryBrokerPipes` at 114 lines, over the 80-line function limit.
  - I fixed it by extracting the host-end tracking into `MFTLibTestExtensions/HostPipeEnds.cs`.

### Files
- Modified:
  - `MFTLib/Broker/Client/`: `BrokerProcess.cs`, `BrokerProcess.Control.cs`, `BrokerProcess.Channels.cs`
    (test hook), `BrokerDriveChannel.cs`.
  - `MFTLibTestExtensions/`: `BrokerTestHarness.cs`, `InMemoryBrokerPipes.cs`, `InMemoryDuplexStream.cs`.
  - `MFTLib.Tests/`: `BrokerProcessTests.cs` (`CreateHost` takes a `timeProvider`; the FailsMidFrame
    expectation), `BrokerProcessTests.Timeouts.cs` (rewritten), `TestSupport/BrokerTestStreams.cs`
    (`ThrowOnAsyncDisposeStream`, `CorruptFrameWrite`).
- Created:
  - `MFTLib.Tests/`: `BrokerProcessTests.Disposal.cs`, `BrokerProcessTests.HostFault.cs`,
    `TestSupport/TimerSignalingClock.cs`.
  - `MFTLibTestExtensions/HostPipeEnds.cs`.

## Fix round 2

Status: DONE_WITH_CONCERNS. Commit `cfb30e6` on task/265-C2 is a new commit on top of `3541d3e`, which was
not amended.

### The `_closing` regression test (re-review Important 1)
Settled by running it:
- I removed `_closing.Cancel()` from `BrokerDriveChannel.DisposeAsync` as a scratch edit; the edit was
  allowed.
- The existing `Dispose_DuringScanTheHostAbandons_FailsScanWithChannelLostAndReleasesSection` still PASSED
  (log: `.superpowers/fix2-scratch-existing.log`). The re-review was right: at `3541d3e` the host's exit
  closes the host end first, so the scan ends through EOF.
- I kept that test, since it still pins the host-exit EOF path, and corrected its comment so it no longer
  claims the channel's own disposal ends the read.
- The regression test for the fix is the new one below.

New `Dispose_DuringScanWithHostEndHeldOpen_EndsPendingReadWithChannelLost`:
- A `ScriptedBroker` host answers the volume query and connects the channel. It reads `ArmAndScan`, writes
  `Cursor`, then keeps its drive end open and writes nothing more.
- The test disposes the process, with a bounded wait, then asserts `BrokerChannelLostException('C')` and
  that the section was released. Every await is bounded by `HangGuard`.
- RED with `_closing.Cancel()` removed:
  `Threw exception TimeoutException, but exception BrokerChannelLostException was expected.` The pending
  read hung. Log: `.superpowers/fix2-red-mutated.log`.
- GREEN once the line was restored (`.superpowers/fix2-restored.log`).

### Terminal frame already read before disposal
New `Dispose_AfterTerminalFrameRead_ScanReturnsItsResult`:
- The scripted host writes `Cursor`, `ScanReady` and `JournalBatch` in one write and keeps its end open.
- The test waits until the client has consumed every byte of those frames, observed through a new
  `ReadCounter` wrapper on the client's drive pipe; `ScriptedBroker` gained an optional drive-pipe wrapper
  for this. Only then does it dispose the process.
- RED on the current code, with `_closing` intact: the scan threw `BrokerChannelLostException` ("Drive C
  broker channel failed: Cannot access a disposed object"). The terminal frame had been read, but disposal
  cut off the read that waits for the host to close the channel, and the finished scan was discarded.
  That is a defect, so I fixed it here.
- Fix (`BrokerProcess.Scan.cs`, `ReadTerminalAsync`): a `BrokerChannelLostException` from that read
  returns the terminal frame. An actual extra frame is still a protocol error (the existing
  `ScanDrive_JournalBatchAfterCatchUpLost_IsProtocolError` still passes), and the caller's own
  cancellation still propagates.
- GREEN: the result carries the advanced cursor `(7, 1500)` and the armed cursor.

### DisposeAsync doc comment (re-review Minor 1)
Comment text only. It now says:
- how the broker ended never makes `DisposeAsync` throw;
- a control pipe that fails to close is only logged;
- an exception thrown by an `Ended` handler, or by closing a drive channel, does propagate.

I judge both remaining escapes to be defects rather than documentation matters, and did not fix them
here:
1. A throwing `Ended` handler faults the control reader. `End` invokes `Ended` without a guard, so the
   exception also escapes `DisposeAsync` (through `await _controlReader`). It also runs after pending
   requests were failed, so nothing is lost, but a consumer's handler bug surfaces from an unrelated
   dispose.
2. A `channel.DisposeAsync` that throws inside `CloseChannelsAsync` stops the sweep, so the remaining
   channels stay open and the control reader is not joined.
Both belong to the final review's Minor list (previous review Minor 5; re-review Minor 1).

### Scratch edits
Reverted: `git diff HEAD -- MFTLib/Broker/Client/BrokerDriveChannel.cs` was empty before the commit, and
`git status --short` is clean after it.

### Verification
- Targeted:
  `dotnet test ... --filter "FullyQualifiedName~BrokerProcessTests|FullyQualifiedName~BrokerProcessLaunchTests|FullyQualifiedName~BrokerFrameLengthTests|FullyQualifiedName~JournalBrokerHostChannelTests"`
  gives `Passed! - Failed: 0, Passed: 100, Total: 100`, three runs in a row.
- `run-coverage.ps1 -NonInteractive`: `Total tests: 1367, Passed: 1361, Skipped: 6` (0 failed); line
  coverage 96%.
- `aislop scan .`: `99 / 100`, 6 warnings, all the same ones as on base `cb13c86`.

### Files
- `MFTLib/Broker/Client/BrokerProcess.Scan.cs` (the terminal fix)
- `MFTLib/Broker/Client/BrokerProcess.cs` (the comment)
- `MFTLib.Tests/BrokerProcessTests.Disposal.cs` (two new tests, and the corrected comment on the kept test)
- `MFTLib.Tests/TestSupport/BrokerTestStreams.cs` (`ReadCounter`)
- `MFTLib.Tests/TestSupport/ScriptedBroker.cs` (the optional drive-pipe wrapper)
